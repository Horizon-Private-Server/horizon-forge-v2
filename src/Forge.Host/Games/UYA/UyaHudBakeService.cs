using System.Security.Cryptography;
using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Hud;
using RatchetPs2.Core.IO;
using RatchetPs2.Core.Textures.Pif;
using RatchetPs2.Core.Wad;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaHudBakeService
{
    private const string HeaderSectionName = "level-data/hud-header";

    public static async Task<BakeLayerInput> CreateBakeInputAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot, cancellationToken);
        var hud = workspace.Content.Hud;
        var overrides = RelevantOverrides(workspace, hud);
        var sourceFiles = new List<UyaHudSourceFingerprint>();
        var blockers = new List<string>();
        if (hud is not null)
        {
            try
            {
                var source = await ReadSourceAsync(projectRoot, cancellationToken);
                sourceFiles.AddRange(source.Records.Select(value =>
                    new UyaHudSourceFingerprint(value.Name, value.Checksum, value.Size)));
                _ = ReadHud(source.Header, source.Banks);
                foreach (var edit in ReplacementEdits(workspace, hud))
                    _ = await ReadTextureAsync(workspace, edit.Effective, cancellationToken);
                foreach (var addition in hud.Additions)
                    _ = await ReadTextureAsync(workspace, addition.Texture, cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException
                or UnauthorizedAccessException or NotSupportedException or OverflowException)
            {
                blockers.Add($"HUD source or custom texture is invalid ({exception.Message}); repair the project from the verified source level or replace the texture.");
            }
        }

        var assets = ReplacementEdits(workspace, hud).Select(value => value.Effective.Id)
            .Concat(hud?.Additions.Select(value => value.Texture.Id) ?? [])
            .Distinct()
            .OrderBy(value => value.ToString(), StringComparer.Ordinal)
            .ToArray();
        var fingerprint = new UyaHudBakeFingerprint(
            UyaHudBakeSchema.CurrentVersion,
            hud,
            overrides,
            sourceFiles.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray());
        return new(
            BakeLayerId.Hud,
            ForgeProjectPersistence.Serialize(fingerprint),
            assets,
            ReadOnlyMemory<byte>.Empty,
            blockers);
    }

    public static async Task<BakeLayerSnapshot> StageAsync(
        string projectRoot,
        BakeStagingStore staging,
        BakeLayerPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Layer != BakeLayerId.Hud)
            throw new ArgumentException("HUD staging requires the HUD layer plan.", nameof(plan));

        var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot, cancellationToken);
        if (workspace.Content.Hud is not { } hud)
        {
            var unavailable = new UyaHudBakeManifest(UyaHudBakeSchema.CurrentVersion, false, false, []);
            return await staging.CommitAsync(plan, (output, token) => WriteManifestAsync(output, unavailable, token),
                cancellationToken);
        }

        var source = await ReadSourceAsync(projectRoot, cancellationToken);
        var replacements = new List<HudTextureReplacement>();
        foreach (var edit in ReplacementEdits(workspace, hud))
        {
            var texture = await ReadTextureAsync(workspace, edit.Effective, cancellationToken);
            foreach (var frame in edit.Frames)
                replacements.Add(new(frame.SourceFrameIndex, texture));
        }
        var additions = new List<HudIconAddition>(hud.Additions.Count);
        foreach (var addition in hud.Additions)
            additions.Add(new(
                addition.SpriteId,
                addition.BankIndex,
                await ReadTextureAsync(workspace, addition.Texture, cancellationToken)));

        var composition = await Task.Run(() => HudComposer.Compose(
            GameId.UYA,
            source.Header,
            source.Banks,
            replacements,
            additions,
            cancellationToken: cancellationToken), cancellationToken);
        var changedBanks = composition.BankBytes
            .Select((bytes, index) => (bytes, index))
            .Where(value => !value.bytes.SequenceEqual(source.Banks[value.index]))
            .Select(value => value.index)
            .ToArray();
        var manifest = new UyaHudBakeManifest(
            UyaHudBakeSchema.CurrentVersion,
            true,
            !composition.HeaderBytes.SequenceEqual(source.Header),
            changedBanks);

        return await staging.CommitAsync(plan, async (output, token) =>
        {
            await WriteManifestAsync(output, manifest, token);
            await ForgeProjectPersistence.WriteFileSafelyAsync(
                Path.Combine(output, "header.bin"), composition.HeaderBytes, token);
            for (var index = 0; index < composition.BankBytes.Count; index++)
                await ForgeProjectPersistence.WriteFileSafelyAsync(
                    Path.Combine(output, BankFileName(index)), composition.BankBytes[index], token);
        }, async (output, token) =>
        {
            var staged = await ReadStagedAsync(output, token);
            if (staged.Manifest.SchemaVersion != manifest.SchemaVersion
                || staged.Manifest.Available != manifest.Available
                || staged.Manifest.HeaderChanged != manifest.HeaderChanged
                || !staged.Manifest.ChangedBanks.SequenceEqual(manifest.ChangedBanks)
                || !staged.Header.SequenceEqual(composition.HeaderBytes)
                || staged.Banks.Select((value, index) => value.SequenceEqual(composition.BankBytes[index]))
                    .Any(value => !value))
                throw new InvalidDataException("Staged HUD bytes changed during write.");
            ValidateSemantic(hud, staged.Header, staged.Banks, replacements, additions);
        }, cancellationToken);
    }

    public static async Task AddReplacementsAsync(
        string stagedRoot,
        IDictionary<string, ReadOnlyMemory<byte>> replacements,
        CancellationToken cancellationToken)
    {
        var staged = await ReadStagedAsync(stagedRoot, cancellationToken);
        if (!staged.Manifest.Available) return;
        if (staged.Manifest.HeaderChanged)
            replacements.Add("hud/header.bin", staged.Header);
        foreach (var index in staged.Manifest.ChangedBanks)
            replacements.Add($"hud/bank{index}.bin", BinaryMagic.IsWad(staged.Banks[index])
                ? WadCompression.Decompress(staged.Banks[index])
                : staged.Banks[index]);
    }

    public static void ValidatePacked(string stagedRoot, UyaLevelWadPackage package)
    {
        var manifest = Read<UyaHudBakeManifest>(stagedRoot, UyaHudBakeSchema.ManifestFileName, "staged HUD layer");
        ValidateManifest(manifest);
        if (!manifest.Available) return;
        var files = package.Files.ToDictionary(value => value.Path, StringComparer.Ordinal);
        var header = Required(files, "hud/header.bin");
        var banks = Enumerable.Range(0, ProjectHudSchema.PhysicalBankCount)
            .Select(index => files.TryGetValue($"hud/bank{index}.bin", out var file) ? file.Bytes : [])
            .ToArray();
        _ = ReadHud(header, banks);
    }

    private static IReadOnlyList<ProjectAssetOverride> RelevantOverrides(
        ForgeProjectWorkspace workspace,
        ProjectHudState? hud)
    {
        if (hud is null) return [];
        var sources = hud.SourceIcons.SelectMany(value => value.Frames)
            .Select(value => value.Texture)
            .OfType<ProjectAssetReference>()
            .ToHashSet();
        return workspace.Content.AssetOverrides.Where(value => sources.Contains(value.Source))
            .OrderBy(value => value.Source.Id)
            .ToArray();
    }

    private static IReadOnlyList<ReplacementEdit> ReplacementEdits(
        ForgeProjectWorkspace workspace,
        ProjectHudState? hud)
    {
        if (hud is null) return [];
        return hud.SourceIcons.SelectMany(value => value.Frames)
            .Where(value => value.Texture is not null)
            .GroupBy(value => value.Texture!)
            .Select(group => new ReplacementEdit(
                group.Key,
                workspace.ResolveAssetReference(group.Key),
                group.ToArray()))
            .Where(value => value.Source != value.Effective)
            .OrderBy(value => value.Source.Id)
            .ToArray();
    }

    private static async Task<HudIndexedTexture> ReadTextureAsync(
        ForgeProjectWorkspace workspace,
        ProjectAssetReference reference,
        CancellationToken cancellationToken)
    {
        var bytes = await workspace.ReadAttachedAssetVerifiedAsync(
            reference, ProjectTextureAssetSchema.MaximumCanonicalBytes, cancellationToken);
        var pif = PifReader.Read(bytes);
        if (pif.Encoding != PifTextureEncoding.Indexed8 || pif.PaletteData.Length != HudBankReader.PaletteLength
            || pif.IsSwizzled || pif.MipPixelData.Count != 0)
            throw new InvalidDataException(
                $"Custom HUD texture {reference.Id} must be unswizzled indexed-8 with one 256-color palette and no mipmaps.");
        return new(pif.Header.USize, pif.Header.VSize, pif.PaletteData, pif.PixelData);
    }

    private static async Task<SourceHud> ReadSourceAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var inspection = await OpaqueContentStore.InspectAsync(projectRoot, cancellationToken);
        var manifest = inspection.Manifest
            ?? throw new InvalidDataException("Opaque source manifest is missing.");
        var byName = manifest.Sections.ToDictionary(value => value.Name, StringComparer.Ordinal);
        if (!byName.TryGetValue(HeaderSectionName, out var headerRecord))
            throw new InvalidDataException("Imported HUD header is missing.");
        var records = new List<OpaqueSectionRecord> { headerRecord };
        var header = await ReadOpaqueBlobAsync(projectRoot, headerRecord, cancellationToken);
        var banks = new byte[ProjectHudSchema.PhysicalBankCount][];
        for (var index = 0; index < banks.Length; index++)
        {
            if (!byName.TryGetValue(BankSectionName(index), out var record))
            {
                banks[index] = [];
                continue;
            }
            records.Add(record);
            banks[index] = await ReadOpaqueBlobAsync(projectRoot, record, cancellationToken);
        }
        return new(header, banks, records);
    }

    private static async Task<byte[]> ReadOpaqueBlobAsync(
        string projectRoot,
        OpaqueSectionRecord record,
        CancellationToken cancellationToken)
    {
        var opaqueRoot = ForgeProjectPersistence.ResolveRelativePath(projectRoot, OpaqueContentStore.RelativeRootPath);
        var path = ForgeProjectPersistence.ResolveRelativePath(opaqueRoot, record.Blob);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.LongLength != record.Size
            || Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != record.Checksum)
            throw new InvalidDataException($"Imported HUD section {record.Name} failed source validation.");
        return bytes;
    }

    private static HudBankSet ReadHud(byte[] header, IReadOnlyList<byte[]> storedBanks)
    {
        var banks = storedBanks.Select(value => BinaryMagic.IsWad(value)
            ? WadCompression.Decompress(value, new WadDecompressionOptions(64 * 1024 * 1024))
            : value).ToArray();
        return HudBankReader.Read(header, banks);
    }

    private static void ValidateSemantic(
        ProjectHudState expected,
        byte[] header,
        IReadOnlyList<byte[]> banks,
        IReadOnlyList<HudTextureReplacement> replacements,
        IReadOnlyList<HudIconAddition> additions)
    {
        var hud = ReadHud(header, banks);
        var replacementFrames = replacements.Select(value => value.FrameIndex).ToHashSet();
        foreach (var sourceIcon in expected.SourceIcons)
        {
            var icon = hud.Icons.ElementAtOrDefault(sourceIcon.SourceIconIndex)
                ?? throw new InvalidDataException($"HUD source icon {sourceIcon.SourceIconIndex} is missing after composition.");
            if (icon.IconId != sourceIcon.SpriteId
                || icon.FirstFrameIndex != sourceIcon.Frames.FirstOrDefault()?.SourceFrameIndex
                || icon.FrameCount != sourceIcon.Frames.Count)
                throw new InvalidDataException($"HUD source icon {sourceIcon.SourceIconIndex} changed its stable mapping.");
            foreach (var frame in sourceIcon.Frames)
            {
                var actual = hud.Frames.ElementAtOrDefault(frame.SourceFrameIndex)
                    ?? throw new InvalidDataException($"HUD source frame {frame.SourceFrameIndex} is missing after composition.");
                if (!replacementFrames.Contains(frame.SourceFrameIndex)
                    && (actual.PaletteIndex != frame.SourcePaletteIndex
                        || actual.TextureIndex != frame.SourceTextureIndex))
                    throw new InvalidDataException($"HUD source frame {frame.SourceFrameIndex} changed its stable indexes.");
            }
        }
        foreach (var replacement in replacements)
        {
            var frame = hud.Frames.ElementAtOrDefault(replacement.FrameIndex)
                ?? throw new InvalidDataException($"HUD replacement frame {replacement.FrameIndex} is missing.");
            ValidateTexture(hud, frame.PaletteIndex, frame.TextureIndex, replacement.Texture, null);
        }
        foreach (var addition in additions)
        {
            var icon = hud.Icons.SingleOrDefault(value => value.IconId == addition.SpriteId)
                ?? throw new InvalidDataException($"HUD addition {addition.SpriteId:X4} is missing after composition.");
            if (icon.FrameCount != 1)
                throw new InvalidDataException($"HUD addition {addition.SpriteId:X4} has an invalid frame mapping.");
            var frame = hud.Frames[icon.FirstFrameIndex];
            ValidateTexture(hud, frame.PaletteIndex, frame.TextureIndex, addition.Texture, addition.BankIndex);
        }
    }

    private static void ValidateTexture(
        HudBankSet hud,
        int paletteIndex,
        int textureIndex,
        HudIndexedTexture expected,
        int? expectedBank)
    {
        var palette = hud.Palettes.ElementAtOrDefault(paletteIndex);
        var texture = hud.Textures.ElementAtOrDefault(textureIndex);
        if (palette is null || texture is null || !palette.IsLengthValid || !texture.IsLengthValid
            || texture.Width != expected.Width || texture.Height != expected.Height
            || !palette.PaletteBytes.AsSpan().SequenceEqual(expected.PaletteBytes.Span)
            || !texture.PixelBytes.AsSpan().SequenceEqual(expected.PixelBytes.Span)
            || expectedBank is not null && (palette.BankIndex != expectedBank || texture.BankIndex != expectedBank))
            throw new InvalidDataException($"HUD texture {textureIndex} failed semantic re-read validation.");
    }

    private static async Task<StagedHud> ReadStagedAsync(string root, CancellationToken cancellationToken)
    {
        var manifest = ForgeProjectPersistence.Deserialize<UyaHudBakeManifest>(
            await File.ReadAllBytesAsync(Path.Combine(root, UyaHudBakeSchema.ManifestFileName), cancellationToken),
            "staged HUD layer");
        ValidateManifest(manifest);
        if (!manifest.Available) return new(manifest, [], []);
        var header = await File.ReadAllBytesAsync(Path.Combine(root, "header.bin"), cancellationToken);
        var banks = new byte[ProjectHudSchema.PhysicalBankCount][];
        for (var index = 0; index < banks.Length; index++)
            banks[index] = await File.ReadAllBytesAsync(Path.Combine(root, BankFileName(index)), cancellationToken);
        return new(manifest, header, banks);
    }

    private static void ValidateManifest(UyaHudBakeManifest manifest)
    {
        if (manifest.SchemaVersion != UyaHudBakeSchema.CurrentVersion
            || manifest.ChangedBanks is null
            || manifest.ChangedBanks.Any(value => value < 0 || value >= ProjectHudSchema.PhysicalBankCount)
            || manifest.ChangedBanks.Distinct().Count() != manifest.ChangedBanks.Count
            || !manifest.Available && (manifest.HeaderChanged || manifest.ChangedBanks.Count != 0))
            throw new InvalidDataException("Staged HUD manifest is invalid.");
    }

    private static Task WriteManifestAsync(
        string output,
        UyaHudBakeManifest manifest,
        CancellationToken cancellationToken) => ForgeProjectPersistence.WriteFileSafelyAsync(
            Path.Combine(output, UyaHudBakeSchema.ManifestFileName),
            ForgeProjectPersistence.Serialize(manifest),
            cancellationToken);

    private static byte[] Required(IReadOnlyDictionary<string, PackedFile> files, string path) =>
        files.TryGetValue(path, out var file)
            ? file.Bytes
            : throw new InvalidDataException($"Packed output is missing {path}.");

    private static T Read<T>(string root, string relativePath, string description) =>
        ForgeProjectPersistence.Deserialize<T>(
            File.ReadAllBytes(ForgeProjectPersistence.ResolveRelativePath(root, relativePath)), description);

    private static string BankSectionName(int index) => $"level-data/hud-bank-{index}";
    private static string BankFileName(int index) => $"bank{index}.bin";

    private sealed record ReplacementEdit(
        ProjectAssetReference Source,
        ProjectAssetReference Effective,
        IReadOnlyList<ProjectHudSourceFrame> Frames);

    private sealed record SourceHud(
        byte[] Header,
        IReadOnlyList<byte[]> Banks,
        IReadOnlyList<OpaqueSectionRecord> Records);

    private sealed record StagedHud(
        UyaHudBakeManifest Manifest,
        byte[] Header,
        IReadOnlyList<byte[]> Banks);
}

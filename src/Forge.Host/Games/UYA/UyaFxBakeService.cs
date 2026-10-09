using Forge.Host.Domain;
using RatchetPs2.Core.Fx;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Textures.Pif;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaFxBakeService
{
    public static async Task<BakeLayerInput> CreateBakeInputAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot, cancellationToken);
        if (workspace.Content.Fx is not { } fx)
            return new(BakeLayerId.Fx, ReadOnlyMemory<byte>.Empty, [], ReadOnlyMemory<byte>.Empty);
        var source = fx.SourceTextures.Select(value => value.Texture)
            .OfType<ProjectAssetReference>().ToHashSet();
        var overrides = workspace.Content.AssetOverrides
            .Where(value => source.Contains(value.Source))
            .OrderBy(value => value.Source.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
        var assetIds = overrides.Select(value => value.Replacement.Id)
            .Concat(fx.Additions.Select(value => value.Texture.Id))
            .Distinct().ToArray();
        return new(
            BakeLayerId.Fx,
            ForgeProjectPersistence.Serialize(new { Fx = fx, Overrides = overrides }),
            assetIds,
            ReadOnlyMemory<byte>.Empty);
    }

    public static async Task<BakeLayerSnapshot> StageAsync(
        string projectRoot,
        BakeStagingStore staging,
        BakeLayerPlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.Layer != BakeLayerId.Fx)
            throw new ArgumentException("FX staging requires the FX bake layer.", nameof(plan));
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot, cancellationToken);
        return await staging.CommitAsync(
            plan,
            async (output, token) => await WriteAsync(output, workspace, token),
            ValidateStagedAsync,
            cancellationToken);
    }

    public static async Task<FxTextureComposition?> ComposeAsync(
        string stagedRoot,
        byte[] headerBytes,
        byte[] assetBytes,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(stagedRoot, cancellationToken);
        if (!manifest.Available || manifest.Replacements.Count == 0 && manifest.Additions.Count == 0)
            return null;
        var replacements = new List<FxTextureReplacement>(manifest.Replacements.Count);
        foreach (var value in manifest.Replacements)
            replacements.Add(new(value.Index, await ReadTextureAsync(stagedRoot, value, cancellationToken)));
        var additions = new List<FxIndexedTexture>(manifest.Additions.Count);
        foreach (var value in manifest.Additions)
            additions.Add(await ReadTextureAsync(stagedRoot, value, cancellationToken));
        return FxTextureCatalog.Compose(
            GameId.UYA, headerBytes, assetBytes, replacements, additions, cancellationToken: cancellationToken);
    }

    public static async Task ValidatePackedAsync(
        string stagedRoot,
        UyaLevelWadPackage package,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(stagedRoot, cancellationToken);
        if (!manifest.Available) return;
        var files = package.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var inventory = FxTextureCatalog.Read(
            GameId.UYA,
            Required(files, "assets/asset_header.bin"),
            Required(files, "assets/asset_wad.bin"),
            cancellationToken: cancellationToken);
        foreach (var expected in manifest.Replacements.Concat(manifest.Additions))
        {
            var texture = await ReadTextureAsync(stagedRoot, expected, cancellationToken);
            var actual = inventory.Entries.ElementAtOrDefault(expected.Index)
                ?? throw new InvalidDataException($"Packed FX texture {expected.Index} is missing.");
            var canonical = PifReader.Read(actual.CanonicalTextureBytes);
            if (!actual.IsValid
                || actual.Width != texture.Width
                || actual.Height != texture.Height
                || !canonical.PaletteData.AsSpan().SequenceEqual(texture.PaletteBytes.Span)
                || !canonical.PixelData.AsSpan().SequenceEqual(texture.PixelBytes.Span))
                throw new InvalidDataException($"Packed FX texture {expected.Index} failed semantic validation.");
        }
    }

    private static async Task WriteAsync(
        string output,
        ForgeProjectWorkspace workspace,
        CancellationToken cancellationToken)
    {
        if (workspace.Content.Fx is not { } fx)
        {
            await WriteManifestAsync(output, new(UyaFxBakeSchema.CurrentVersion, false, [], []), cancellationToken);
            return;
        }

        var replacements = new List<UyaFxBakeTexture>();
        foreach (var source in fx.SourceTextures.Where(value => value.Texture is not null))
        {
            var effective = workspace.ResolveAssetReference(source.Texture!);
            if (effective == source.Texture) continue;
            replacements.Add(await WriteTextureAsync(
                output, workspace, source.SourceIndex, effective, cancellationToken));
        }
        var additions = new List<UyaFxBakeTexture>(fx.Additions.Count);
        for (var offset = 0; offset < fx.Additions.Count; offset++)
            additions.Add(await WriteTextureAsync(
                output,
                workspace,
                fx.SourceTextures.Count + offset,
                fx.Additions[offset].Texture,
                cancellationToken));
        await WriteManifestAsync(
            output,
            new(UyaFxBakeSchema.CurrentVersion, true, replacements, additions),
            cancellationToken);
    }

    private static async Task<UyaFxBakeTexture> WriteTextureAsync(
        string output,
        ForgeProjectWorkspace workspace,
        int index,
        ProjectAssetReference reference,
        CancellationToken cancellationToken)
    {
        var bytes = await workspace.ReadAttachedAssetVerifiedAsync(
            reference, ProjectTextureAssetSchema.MaximumCanonicalBytes, cancellationToken);
        var texture = ReadTexture(bytes, reference.Id.ToString());
        var resource = $"textures/{reference.Id}.pif";
        var path = ForgeProjectPersistence.ResolveRelativePath(output, resource);
        if (!File.Exists(path))
            await ForgeProjectPersistence.WriteFileSafelyAsync(path, bytes, cancellationToken);
        return new(index, texture.Width, texture.Height, reference.Id.ToString(), resource);
    }

    private static async Task ValidateStagedAsync(string output, CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(output, cancellationToken);
        foreach (var value in manifest.Replacements.Concat(manifest.Additions))
            _ = await ReadTextureAsync(output, value, cancellationToken);
    }

    private static async Task<UyaFxBakeManifest> ReadManifestAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var manifest = ForgeProjectPersistence.Deserialize<UyaFxBakeManifest>(
            await File.ReadAllBytesAsync(Path.Combine(root, UyaFxBakeSchema.ManifestFileName), cancellationToken),
            "staged FX layer");
        var replacementIndexes = manifest.Replacements?.Select(value => value.Index).ToArray() ?? [];
        var additionIndexes = manifest.Additions?.Select(value => value.Index).ToArray() ?? [];
        if (manifest.SchemaVersion != UyaFxBakeSchema.CurrentVersion
            || manifest.Replacements is null || manifest.Additions is null
            || !manifest.Available && (manifest.Replacements.Count != 0 || manifest.Additions.Count != 0)
            || replacementIndexes.Length + additionIndexes.Length > ProjectFxSchema.MaximumTextureCount
            || replacementIndexes.Distinct().Count() != replacementIndexes.Length
            || additionIndexes.Distinct().Count() != additionIndexes.Length
            || !additionIndexes.SequenceEqual(
                additionIndexes.Length == 0 ? [] : Enumerable.Range(additionIndexes[0], additionIndexes.Length))
            || replacementIndexes.Intersect(additionIndexes).Any()
            || additionIndexes.Length != 0 && replacementIndexes.Any(value => value >= additionIndexes[0])
            || manifest.Replacements.Concat(manifest.Additions).Any(value =>
                value.Index < 0 || value.Width <= 0 || value.Height <= 0
                || value.Width > 4_096 || value.Height > 4_096
                || (value.Width & (value.Width - 1)) != 0
                || (value.Height & (value.Height - 1)) != 0
                || !IsAssetId(value.AssetId)))
            throw new InvalidDataException("Staged FX manifest is invalid.");
        return manifest;
    }

    private static async Task<FxIndexedTexture> ReadTextureAsync(
        string root,
        UyaFxBakeTexture value,
        CancellationToken cancellationToken)
    {
        var path = ForgeProjectPersistence.ResolveRelativePath(root, value.Resource);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > ProjectTextureAssetSchema.MaximumCanonicalBytes)
            throw new InvalidDataException($"Staged FX texture {value.Index} has an invalid size.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var id = AssetId.Parse(value.AssetId);
        if (AssetId.Compute(AssetKind.Texture, ProjectTextureAssetSchema.CanonicalFormatVersion, bytes) != id)
            throw new InvalidDataException($"Staged FX texture {value.Index} failed integrity validation.");
        var texture = ReadTexture(bytes, value.AssetId);
        if (texture.Width != value.Width || texture.Height != value.Height)
            throw new InvalidDataException($"Staged FX texture {value.Index} metadata is inconsistent.");
        return texture;
    }

    private static FxIndexedTexture ReadTexture(byte[] bytes, string label)
    {
        var pif = PifReader.Read(bytes);
        if (pif.Encoding != PifTextureEncoding.Indexed8 || pif.PaletteData.Length != 0x400
            || pif.IsSwizzled || pif.MipPixelData.Count != 0)
            throw new InvalidDataException(
                $"Custom FX texture {label} must be unswizzled indexed-8 with one 256-color palette and no mipmaps.");
        return new(pif.Header.USize, pif.Header.VSize, pif.PaletteData, pif.PixelData);
    }

    private static Task WriteManifestAsync(
        string output,
        UyaFxBakeManifest manifest,
        CancellationToken cancellationToken) => ForgeProjectPersistence.WriteFileSafelyAsync(
            Path.Combine(output, UyaFxBakeSchema.ManifestFileName),
            ForgeProjectPersistence.Serialize(manifest),
            cancellationToken);

    private static byte[] Required(IReadOnlyDictionary<string, RatchetPs2.Core.Wad.Models.PackedFile> files, string path) =>
        files.TryGetValue(path, out var file)
            ? file.Bytes
            : throw new InvalidDataException($"Packed output is missing {path}.");

    private static bool IsAssetId(string value)
    {
        try { return AssetId.Parse(value).ToString() == value; }
        catch (Exception exception) when (exception is ArgumentNullException or FormatException) { return false; }
    }
}

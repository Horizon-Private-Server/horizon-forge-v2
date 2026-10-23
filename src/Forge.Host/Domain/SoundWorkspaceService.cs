using System.Security.Cryptography;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Sounds;
using RatchetPs2.Sdk;

namespace Forge.Host.Domain;

internal static class SoundWorkspaceService
{
    private const string SoundBankSection = "level/sound-bank";

    public static async Task<EditorSoundWorkspace> ReadAsync(
        ForgeProjectWorkspace workspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var state = workspace.Content.Sounds;
        var targetGame = TargetGame(workspace);
        var stalePreviewState = state.Programs.Any(value => !value.PreviewSupported);
        var currentPrograms = stalePreviewState
            ? await ReadCurrentProgramsAsync(workspace, cancellationToken)
            : null;
        var samples = state.Samples.ToDictionary(value => value.Asset.Id);
        var globalSlots = state.GlobalRemaps
            .Where(value => value.NativeProgramIndex >= 0)
            .GroupBy(value => value.NativeProgramIndex)
            .ToDictionary(group => group.Key, group => group.Select(value => value.Slot).Order().ToArray());
        var mobyDependents = state.MobyRemaps
            .SelectMany(map => map.Slots.Where(slot => slot.NativeProgramIndex >= 0).Select(slot => new
            {
                slot.NativeProgramIndex,
                map.ClassId,
                MobyAssetId = map.Moby?.Id.ToString(),
                slot.Slot,
            }))
            .GroupBy(value => value.NativeProgramIndex)
            .ToDictionary(group => group.Key, group => group
                .GroupBy(value => (value.ClassId, value.MobyAssetId))
                .Select(values => new EditorSoundMobyDependent(
                    values.Key.ClassId,
                    values.Key.MobyAssetId,
                    values.Select(value => value.Slot).Order().ToArray()))
                .OrderBy(value => value.ClassId)
                .ToArray());

        var programs = state.Programs.Select(program =>
        {
            var current = currentPrograms?.GetValueOrDefault(program.Provenance.NativeIndex);
            var programSamples = program.Operations
                .Where(value => value.Tone is not null)
                .Select(value => (Operation: value, Tone: value.Tone!))
                .DistinctBy(value => value.Tone.Sample.Id)
                .Select(value =>
                {
                    var sample = samples[value.Tone.Sample.Id];
                    return new EditorSoundSample(
                        sample.Asset.Id.ToString(),
                        sample.SourceContentId,
                        sample.EncodedLength,
                        sample.DecodedFrameCount,
                        sample.IsLooping,
                        sample.LoopStartFrame,
                        sample.LoopEndFrame,
                        SoundBankReader.PreviewSampleRate(
                            targetGame, value.Tone.CenterNote, value.Tone.CenterFine),
                        1);
                }).ToArray();
            return new EditorSoundProgram(
                program.Asset?.Id.ToString() ?? (current?.ContentId is { } contentId
                    ? ProjectSounds.ProgramId(contentId).ToString()
                    : null),
                program.SourceContentId ?? current?.ContentId,
                program.Provenance.NativeIndex,
                program.Provenance.Origin.ToString(),
                program.Provenance.Game,
                program.Provenance.Level,
                program.Provenance.BankSha256,
                null,
                program.VolumeGroup,
                globalSlots.TryGetValue(program.Provenance.NativeIndex, out var globals) ? globals : [],
                mobyDependents.TryGetValue(program.Provenance.NativeIndex, out var dependents) ? dependents : [],
                programSamples,
                HasRandomTriggerGate(program.Operations),
                current?.PreviewSupported ?? program.PreviewSupported,
                current is null ? program.PreviewBlocker : current.PreviewBlocker);
        }).OrderBy(value => value.NativeIndex).ToArray();

        return new(
            programs,
            state.Samples.Count,
            state.MobyRemaps.Count,
            state.GlobalRemaps.Count(value => value.NativeProgramIndex >= 0));
    }

    public static async Task<EditorSoundPreview> RenderPreviewAsync(
        ForgeProjectWorkspace workspace,
        int nativeIndex,
        bool preferAudible,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var program = workspace.Content.Sounds.Programs.SingleOrDefault(
            value => value.Provenance.NativeIndex == nativeIndex)
            ?? throw new ArgumentOutOfRangeException(nameof(nativeIndex), "Sound program does not exist.");
        var bank = await ReadSoundBankAsync(workspace.RootPath, cancellationToken);
        if (!string.Equals(
                Convert.ToHexString(SHA256.HashData(bank)).ToLowerInvariant(),
                program.Provenance.BankSha256,
                StringComparison.Ordinal))
            throw new InvalidDataException("The retained sound bank does not match this program's source.");
        var preview = SoundBankReader.RenderPreview(
            TargetGame(workspace), bank, nativeIndex,
            preferAudible: preferAudible, cancellationToken: cancellationToken);
        return ToEditorPreview(
            preview, program.Asset?.Id ?? ProjectSounds.ProgramId(preview.ProgramId));
    }

    public static async Task<EditorSoundPreview> RenderCatalogPreviewAsync(
        string catalogRootPath,
        AssetId bankAssetId,
        string gameName,
        int nativeIndex,
        bool preferAudible,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameName);
        if (!Enum.TryParse<GameId>(gameName, ignoreCase: true, out var game) || !Enum.IsDefined(game))
            throw new NotSupportedException($"Sound preview is not supported for {gameName}.");
        var catalog = await AssetCatalogStore.OpenReadOnlyAsync(catalogRootPath, cancellationToken);
        var entry = catalog.Query(new(Id: bankAssetId)).SingleOrDefault()
            ?? throw new KeyNotFoundException($"Sound bank {bankAssetId} is not present in the catalog.");
        if (entry.Kind != AssetKind.SoundBank
            || entry.CanonicalFormatVersion != SoundBankAssetSchema.CanonicalFormatVersion)
            throw new InvalidDataException($"Asset {bankAssetId} is not a supported sound bank.");
        var path = catalog.ResolveBlobPath(bankAssetId)
            ?? throw new FileNotFoundException($"Sound bank blob {bankAssetId} is missing.");
        var bank = await AssetCatalogBlobReader.ReadVerifiedAsync(
            entry, path, ForgeProjectWorkspace.MaxAttachedAssetBytes, cancellationToken);
        var preview = SoundBankReader.RenderPreview(
            game, bank, nativeIndex, preferAudible: preferAudible, cancellationToken: cancellationToken);
        return ToEditorPreview(preview, ProjectSounds.ProgramId(preview.ProgramId));
    }

    private static EditorSoundPreview ToEditorPreview(SoundPcmPreview preview, AssetId assetId)
    {
        var pcm = new byte[checked(preview.Pcm16.Length * sizeof(short))];
        Buffer.BlockCopy(preview.Pcm16, 0, pcm, 0, pcm.Length);
        return new(
            preview.ProgramIndex,
            assetId.ToString(),
            preview.SampleId,
            preview.SampleRate,
            preview.Channels,
            pcm,
            preview.IsLooping,
            preview.LoopStartFrame,
            preview.LoopEndFrame,
            preview.Gain,
            preview.PanDegrees,
            preview.IsVariable);
    }

    private static bool HasRandomTriggerGate(IReadOnlyList<ProjectSoundOperation> operations)
    {
        for (var index = 0; index <= operations.Count - 3; index++)
            if (operations[index] is { Type: 0x1f } random
                && operations[index + 1] is { Type: 0x22 } test
                && operations[index + 2].Type == 0x18
                && random.Argument0 == test.Argument0) return true;
        return false;
    }

    private static async Task<IReadOnlyDictionary<int, RatchetPs2.Core.Sounds.SoundProgramInfo>> ReadCurrentProgramsAsync(
        ForgeProjectWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var bank = await ReadSoundBankAsync(workspace.RootPath, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(bank)).ToLowerInvariant();
        if (workspace.Content.Sounds.Programs.Any(value => value.Provenance.BankSha256 != hash))
            throw new InvalidDataException("The retained sound bank does not match the project sound inventory.");
        return SoundBankReader.Inspect(TargetGame(workspace), bank, cancellationToken).Programs
            .ToDictionary(value => value.NativeIndex);
    }

    private static GameId TargetGame(ForgeProjectWorkspace workspace) =>
        Enum.TryParse<GameId>(workspace.Manifest.Target.Game, ignoreCase: true, out var game)
            ? game
            : throw new NotSupportedException(
                $"Sound preview is not supported for {workspace.Manifest.Target.Game}.");

    private static async Task<byte[]> ReadSoundBankAsync(string projectRoot, CancellationToken cancellationToken)
    {
        var inspection = await OpaqueContentStore.InspectAsync(projectRoot, cancellationToken);
        if (!inspection.IsValid) throw new InvalidDataException(string.Join(' ', inspection.Blockers));
        var record = inspection.Manifest!.Sections.SingleOrDefault(value => value.Name == SoundBankSection)
            ?? throw new InvalidDataException("The retained sound-bank section is missing; repair the project first.");
        var opaqueRoot = ForgeProjectPersistence.ResolveRelativePath(projectRoot, OpaqueContentStore.RelativeRootPath);
        var path = ForgeProjectPersistence.ResolveRelativePath(opaqueRoot, record.Blob);
        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

}

using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Sounds;
using RatchetPs2.Sdk;
using Forge.Host.Domain;
using System.Runtime.CompilerServices;

namespace Forge.Host.Games.UYA;

internal sealed record UyaSoundCompositionResult(
    byte[] BankBytes,
    SoundRemapComposition Remap,
    bool BankChanged,
    bool RemapChanged,
    UyaSoundCompositionPlan Plan);

internal sealed record UyaSoundCompositionPlan(
    int RetainedProgramCount,
    IReadOnlyList<AssetId> ReusedPrograms,
    IReadOnlyList<AssetId> AppendedPrograms,
    IReadOnlyDictionary<AssetId, int> ProgramSlots,
    IReadOnlyList<AssetId> AppendedSamples,
    int FinalBankBytes);

internal static class UyaSoundCompositionService
{
    private static readonly ConditionalWeakTable<ForgeProjectWorkspace, PreviewCache> PreviewCaches = new();

    private sealed class PreviewCache
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal ProjectSoundState? State;
        internal ProjectSoundPreviewBank? Bank;
    }
    public static async Task<UyaSoundCompositionResult> ComposeAsync(
        ForgeProjectWorkspace workspace,
        byte[] sourceBankBytes,
        byte[] sourceHeaderBytes,
        byte[] targetHeaderBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(sourceBankBytes);
        ArgumentNullException.ThrowIfNull(sourceHeaderBytes);
        ArgumentNullException.ThrowIfNull(targetHeaderBytes);
        var state = UyaMobySoundImportService.NormalizeLegacyRemaps(workspace.Content.Sounds);
        var header = LevelAssetReader.ReadHeader(targetHeaderBytes);
        var classes = LevelAssetReader.ReadModelDefinitions(
            targetHeaderBytes, header.MobyModelOffset, header.MobyModelCount)
            .Select(value => value.ModelId).ToHashSet();
        // Deleted placements retain their sound state for undo, but have no packed model.
        var packedMaps = state.MobyRemaps.Where(value => classes.Contains(value.ClassId)).ToArray();
        var requiredPrograms = RequiredPrograms(state, packedMaps, cancellationToken);
        var composed = await ComposeBankAsync(workspace, sourceBankBytes, requiredPrograms, cancellationToken);
        var sourceProgramCount = composed.Plan.RetainedProgramCount;
        var indexByAsset = composed.Plan.ProgramSlots;
        var slotsByClass = packedMaps.ToDictionary(
            value => value.ClassId,
            value => (IReadOnlyList<int>)value.Slots.OrderBy(slot => slot.Slot)
                .Select(slot => ResolveSlot(slot, indexByAsset, sourceProgramCount)).ToArray());
        var globals = state.GlobalRemaps.OrderBy(value => value.Slot)
            .Select(value => ResolveSlot(value, indexByAsset, sourceProgramCount)).ToArray();
        var other = state.OpaqueOtherRange is not { } range
            ? []
            : sourceHeaderBytes.AsSpan(range.Offset, range.Length).ToArray();
        if (other.Length != checked(state.OpaqueOtherRecordCount * 0x20))
            throw new InvalidDataException("Project sound-remap opaque records do not match their retained source range.");
        var remap = new SoundRemapComposition(composed.Programs.Count, slotsByClass, globals, other);
        var remapChanged = RemapChanged(targetHeaderBytes, sourceProgramCount, remap);
        return new(composed.BankBytes, remap, composed.BankChanged, remapChanged, composed.Plan);
    }

    public static async Task<ProjectSoundPreviewBank?> PreparePreviewAsync(
        ForgeProjectWorkspace workspace, CancellationToken token)
    {
        var cache = PreviewCaches.GetValue(workspace, _ => new());
        await cache.Gate.WaitAsync(token);
        try
        {
            var state = workspace.Content.Sounds;
            if (ReferenceEquals(cache.State, state)) return cache.Bank;
            var bank = await CreatePreviewAsync(workspace, token);
            cache.State = state;
            cache.Bank = bank;
            return bank;
        }
        finally { cache.Gate.Release(); }
    }

    private static async Task<ProjectSoundPreviewBank?> CreatePreviewAsync(
        ForgeProjectWorkspace workspace, CancellationToken token)
    {
        var state = UyaMobySoundImportService.NormalizeLegacyRemaps(workspace.Content.Sounds);
        if (ReferenceEquals(state, workspace.Content.Sounds)
            && state.Programs.All(value => value.Provenance.Origin == ProjectSoundOrigin.Base)) return null;
        var composed = await ComposeBankAsync(workspace,
            await SoundWorkspaceService.ReadSoundBankAsync(workspace.RootPath, token), null, token,
            enforceSpuBudget: false);
        ProjectSoundRemapSlot Slot(ProjectSoundRemapSlot value) => value.Sound is { } sound
            ? value with { NativeProgramIndex = composed.Plan.ProgramSlots[sound.Id] } : value;
        return new(composed.BankBytes, state with
        {
            Programs = composed.Programs.Select((program, index) => program with
            {
                Provenance = program.Provenance with { NativeIndex = index },
            }).ToArray(),
            MobyRemaps = state.MobyRemaps.Select(map => map with { Slots = map.Slots.Select(Slot).ToArray() }).ToArray(),
            GlobalRemaps = state.GlobalRemaps.Select(Slot).ToArray(),
        });
    }

    private sealed record BankComposition(byte[] BankBytes, bool BankChanged,
        UyaSoundCompositionPlan Plan, IReadOnlyList<ProjectSoundProgram> Programs);

    private static IReadOnlySet<AssetId> RequiredPrograms(
        ProjectSoundState state, IReadOnlyList<ProjectMobySoundMap> packedMaps, CancellationToken token)
    {
        var programs = state.Programs.Where(value => value.Asset is not null)
            .GroupBy(value => value.Asset!.Id).ToDictionary(value => value.Key, value => value.First());
        var basePrograms = state.Programs.Where(value => value.Provenance.Origin == ProjectSoundOrigin.Base);
        var pending = new Stack<AssetId>(packedMaps.SelectMany(value => value.Slots)
            .Concat(state.GlobalRemaps).Where(value => value.Sound is not null).Select(value => value.Sound!.Id)
            .Concat(basePrograms.Where(value => value.Asset is not null).Select(value => value.Asset!.Id))
            .Concat(basePrograms.SelectMany(value => value.Operations)
                .Where(value => value.ReferencedProgram is not null).Select(value => value.ReferencedProgram!.Id)));
        var required = new HashSet<AssetId>();
        while (pending.TryPop(out var id))
        {
            token.ThrowIfCancellationRequested();
            if (!required.Add(id)) continue;
            if (!programs.TryGetValue(id, out var program))
                throw new InvalidDataException($"Project sound graph contains a missing program dependency {id}.");
            foreach (var operation in program.Operations)
                if (operation.ReferencedProgram is { } reference) pending.Push(reference.Id);
        }
        return required;
    }

    private static async Task<BankComposition> ComposeBankAsync(
        ForgeProjectWorkspace workspace, byte[] sourceBankBytes,
        IReadOnlySet<AssetId>? requiredPrograms, CancellationToken cancellationToken, bool enforceSpuBudget = true)
    {
        var state = workspace.Content.Sounds;
        if (state.Programs.Count == 0)
            throw new InvalidDataException("Project sound inventory is missing.");
        var source = SoundBankReader.Inspect(GameId.UYA, sourceBankBytes, cancellationToken);
        var basePrograms = state.Programs
            .Where(value => value.Provenance.Origin == ProjectSoundOrigin.Base)
            .ToDictionary(value => value.Provenance.NativeIndex);
        if (basePrograms.Count != source.Programs.Count
            || Enumerable.Range(0, source.Programs.Count).Any(index => !basePrograms.ContainsKey(index)))
            throw new InvalidDataException("Project base sound inventory does not match the retained bank.");

        var selected = new List<(string Key, ProjectSoundProgram Program, SoundProgramInfo? Source)>();
        var keyByAsset = new Dictionary<AssetId, string>();
        var reusedPrograms = new List<AssetId>();
        var appendedPrograms = new List<AssetId>();
        foreach (var sourceProgram in source.Programs)
        {
            var program = basePrograms[sourceProgram.NativeIndex];
            var key = $"base:{sourceProgram.NativeIndex}";
            selected.Add((key, program, sourceProgram));
            if (program.Asset is { } asset) keyByAsset.TryAdd(asset.Id, key);
        }
        foreach (var group in state.Programs
            .Where(value => value.Provenance.Origin != ProjectSoundOrigin.Base && value.Asset is { } asset
                && (requiredPrograms is null || requiredPrograms.Contains(asset.Id)))
            .GroupBy(value => value.Asset!.Id)
            .OrderBy(value => value.Key.ToString(), StringComparer.Ordinal))
        {
            if (keyByAsset.ContainsKey(group.Key))
            {
                reusedPrograms.Add(group.Key);
                continue;
            }
            var program = group.First();
            var key = $"asset:{group.Key}";
            keyByAsset.Add(group.Key, key);
            appendedPrograms.Add(group.Key);
            selected.Add((key, program, null));
        }

        var requiredSamples = selected.SelectMany(value => value.Program.Operations)
            .Where(value => value.Tone is not null)
            .Select(value => value.Tone!.Sample.Id).ToHashSet();
        var samples = await BuildSamplesAsync(
            workspace, state, requiredSamples, sourceBankBytes, source, cancellationToken);
        var programInputs = selected.Select(value => ProgramInput(value, keyByAsset)).ToArray();
        var bankChanged = programInputs.Length != source.Programs.Count;
        var bank = bankChanged
            ? SoundBankComposer.Compose(
                GameId.UYA, sourceBankBytes, programInputs, samples, cancellationToken, enforceSpuBudget).BankBytes
            : sourceBankBytes;
        var indexByAsset = new Dictionary<AssetId, int>();
        for (var index = 0; index < selected.Count; index++)
            if (selected[index].Program.Asset is { } asset) indexByAsset.TryAdd(asset.Id, index);

        return new(bank, bankChanged,
            new(
                source.Programs.Count,
                reusedPrograms,
                appendedPrograms,
                indexByAsset,
                state.Samples.Where(value =>
                        requiredSamples.Contains(value.Asset.Id)
                        && value.Provenance.All(provenance => provenance.Origin != ProjectSoundOrigin.Base))
                    .Select(value => value.Asset.Id)
                    .OrderBy(value => value.ToString(), StringComparer.Ordinal).ToArray(),
                bank.Length), selected.Select(value => value.Program).ToArray());
    }

    private static async Task<SoundBankSampleInput[]> BuildSamplesAsync(
        ForgeProjectWorkspace workspace,
        ProjectSoundState state,
        IReadOnlySet<AssetId> requiredSamples,
        byte[] sourceBankBytes,
        SoundBankInventory source,
        CancellationToken cancellationToken)
    {
        var sourceSamples = source.Samples.GroupBy(value => value.ContentId, StringComparer.Ordinal)
            .ToDictionary(value => value.Key, value => value.First(), StringComparer.Ordinal);
        var selectedSamples = state.Samples.Where(value => requiredSamples.Contains(value.Asset.Id)).ToArray();
        if (selectedSamples.Length != requiredSamples.Count)
            throw new InvalidDataException("Project sound graph contains a missing sample dependency.");
        var result = new List<SoundBankSampleInput>(selectedSamples.Length);
        foreach (var sample in selectedSamples.OrderBy(value =>
                     value.Provenance.Any(provenance => provenance.Origin == ProjectSoundOrigin.Base) ? 0 : 1)
                 .ThenBy(value => value.Provenance
                     .Where(provenance => provenance.Origin == ProjectSoundOrigin.Base)
                     .Select(provenance => provenance.NativeIndex).DefaultIfEmpty(int.MaxValue).Min())
                 .ThenBy(value => value.Asset.Id.ToString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes;
            if (sample.Provenance.Any(value => value.Origin == ProjectSoundOrigin.Base))
            {
                if (!sourceSamples.TryGetValue(sample.SourceContentId, out var sourceSample))
                    throw new InvalidDataException(
                        $"Base sound sample {sample.Asset.Id} is absent from the retained bank.");
                bytes = sourceBankBytes.AsSpan(
                    sourceSample.Provenance.Offset, sourceSample.Provenance.Length).ToArray();
            }
            else
            {
                bytes = await workspace.ReadAttachedAssetVerifiedAsync(
                    sample.Asset, ForgeProjectWorkspace.MaxAttachedAssetBytes, cancellationToken);
            }
            if (ProjectSounds.SampleId(bytes) != sample.Asset.Id)
                throw new InvalidDataException($"Sound sample {sample.Asset.Id} has invalid canonical bytes.");
            result.Add(new(sample.Asset.Id.ToString(), bytes));
        }
        return result.ToArray();
    }

    private static SoundBankProgramInput ProgramInput(
        (string Key, ProjectSoundProgram Program, SoundProgramInfo? Source) value,
        IReadOnlyDictionary<AssetId, string> keyByAsset)
    {
        var sourceOperations = value.Source?.Operations.ToDictionary(operation => operation.NativeIndex);
        return new(
            value.Key,
            value.Program.Volume,
            value.Program.VolumeGroup,
            value.Program.Pan,
            value.Program.InstanceLimit,
            value.Program.Flags,
            value.Program.Operations.Select(operation =>
            {
                var parameterBytes = operation.ParameterBytes
                    ?? sourceOperations?.GetValueOrDefault(operation.NativeIndex)?.ParameterBytes
                    ?? throw new InvalidDataException(
                        $"Imported sound {value.Program.Asset?.Id} operation {operation.NativeIndex} "
                        + "is missing its 989snd parameter payload.");
                var referencedKey = operation.ReferencedProgram is { } reference
                    ? keyByAsset.GetValueOrDefault(reference.Id)
                        ?? throw new InvalidDataException(
                            $"Sound {value.Program.Asset?.Id} references missing program {reference.Id}.")
                    : operation.NativeReferencedProgramIndex is { } native
                        && value.Program.Provenance.Origin == ProjectSoundOrigin.Base
                            ? $"base:{native}"
                            : null;
                return new SoundBankOperationInput(
                    operation.Type,
                    operation.Argument0,
                    operation.Argument1,
                    operation.Argument2,
                    operation.Delay,
                    parameterBytes,
                    operation.Tone is not { } tone ? null : new(
                        tone.Priority,
                        tone.Volume,
                        tone.CenterNote,
                        tone.CenterFine,
                        tone.Pan,
                        tone.NoteLow,
                        tone.NoteHigh,
                        tone.PitchBendLow,
                        tone.PitchBendHigh,
                        tone.Adsr1,
                        tone.Adsr2,
                        tone.Flags,
                        tone.Reserved,
                        tone.Sample.Id.ToString()),
                    referencedKey);
            }).ToArray());
    }

    private static int ResolveSlot(
        ProjectSoundRemapSlot slot,
        IReadOnlyDictionary<AssetId, int> indexByAsset,
        int sourceProgramCount)
    {
        if (slot.Sound is { } sound)
            return indexByAsset.TryGetValue(sound.Id, out var index)
                ? index
                : throw new InvalidDataException(
                    $"Sound-remap slot {slot.Slot} references missing program {sound.Id}.");
        if (slot.NativeProgramIndex == -1) return -1;
        if (slot.NativeProgramIndex >= 0 && slot.NativeProgramIndex < sourceProgramCount)
            return slot.NativeProgramIndex;
        throw new InvalidDataException(
            $"Sound-remap slot {slot.Slot} has no importable program: {slot.Blocker}");
    }

    private static bool RemapChanged(
        byte[] sourceHeaderBytes,
        int sourceProgramCount,
        SoundRemapComposition expected)
    {
        var actual = SoundBankReader.InspectRemap(GameId.UYA, sourceHeaderBytes, sourceProgramCount);
        if (!actual.Globals.Select(value => value.NativeProgramIndex).SequenceEqual(expected.GlobalSlots))
            return true;
        var actualByClass = actual.Mobys.ToDictionary(value => value.ClassId);
        return expected.MobySlotsByClassId.Any(pair =>
                !actualByClass.TryGetValue(pair.Key, out var map)
                || !map.Slots.Select(value => value.NativeProgramIndex).SequenceEqual(pair.Value))
            || actualByClass.Any(pair =>
                !expected.MobySlotsByClassId.TryGetValue(pair.Key, out var slots)
                || !pair.Value.Slots.Select(value => value.NativeProgramIndex).SequenceEqual(slots));
    }
}

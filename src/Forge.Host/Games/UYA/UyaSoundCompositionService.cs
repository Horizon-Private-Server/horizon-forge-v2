using RatchetPs2.Core.Games;
using RatchetPs2.Core.Sounds;
using RatchetPs2.Sdk;
using Forge.Host.Domain;

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
    public static async Task<UyaSoundCompositionResult> ComposeAsync(
        ForgeProjectWorkspace workspace,
        byte[] sourceBankBytes,
        byte[] sourceHeaderBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(sourceBankBytes);
        ArgumentNullException.ThrowIfNull(sourceHeaderBytes);
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
            .Where(value => value.Provenance.Origin != ProjectSoundOrigin.Base && value.Asset is not null)
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
                GameId.UYA, sourceBankBytes, programInputs, samples, cancellationToken).BankBytes
            : sourceBankBytes;
        var indexByAsset = new Dictionary<AssetId, int>();
        for (var index = 0; index < selected.Count; index++)
            if (selected[index].Program.Asset is { } asset) indexByAsset.TryAdd(asset.Id, index);

        var slotsByClass = state.MobyRemaps.ToDictionary(
            value => value.ClassId,
            value => (IReadOnlyList<int>)value.Slots.OrderBy(slot => slot.Slot)
                .Select(slot => ResolveSlot(slot, indexByAsset, source.Programs.Count)).ToArray());
        var globals = state.GlobalRemaps.OrderBy(value => value.Slot)
            .Select(value => ResolveSlot(value, indexByAsset, source.Programs.Count)).ToArray();
        var other = state.OpaqueOtherRange is not { } range
            ? []
            : sourceHeaderBytes.AsSpan(range.Offset, range.Length).ToArray();
        if (other.Length != checked(state.OpaqueOtherRecordCount * 0x20))
            throw new InvalidDataException("Project sound-remap opaque records do not match their retained source range.");
        var remap = new SoundRemapComposition(programInputs.Length, slotsByClass, globals, other);
        var remapChanged = RemapChanged(sourceHeaderBytes, source.Programs.Count, remap);
        return new(
            bank,
            remap,
            bankChanged,
            remapChanged,
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
                bank.Length));
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

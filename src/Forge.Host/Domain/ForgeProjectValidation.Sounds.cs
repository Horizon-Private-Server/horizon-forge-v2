namespace Forge.Host.Domain;

internal static partial class ForgeProjectValidation
{
    private static void ValidateSounds(
        ProjectSoundState state,
        IReadOnlyList<ProjectAttachedAsset> assets)
    {
        if (state.SchemaVersion != ProjectSoundSchema.CurrentVersion
            || state.Programs is null || state.Samples is null || state.MobyRemaps is null
            || state.GlobalRemaps is null
            || state.Programs.Count > ProjectSoundSchema.MaximumPrograms
            || state.Samples.Count > ProjectSoundSchema.MaximumSamples
            || state.Programs.Any(value => value is null)
            || state.Samples.Any(value => value is null)
            || state.MobyRemaps.Any(value => value is null)
            || state.GlobalRemaps.Any(value => value is null)
            || state.OpaqueOtherRecordCount < 0)
            throw new InvalidDataException("Project sound state is invalid.");
        ValidateRange(state.OpaqueOtherRange, "sound opaque-other range", nullable: true);
        ValidateRange(state.SourceRemapRange, "sound source-remap range", nullable: true);

        var samples = new Dictionary<AssetId, ProjectSoundSample>();
        foreach (var sample in state.Samples)
        {
            ValidateReference(sample.Asset, AssetKind.SoundSample, "sound sample");
            ValidateContentId(sample.SourceContentId, $"sound sample {sample.Asset.Id}");
            if (sample.Provenance is null || sample.Provenance.Count == 0)
                throw new InvalidDataException($"Sound sample {sample.Asset.Id} provenance is missing.");
            foreach (var provenance in sample.Provenance)
                ValidateProvenance(provenance, $"sound sample {sample.Asset.Id}");
            if (!samples.TryAdd(sample.Asset.Id, sample))
                throw new InvalidDataException($"Project contains duplicate sound sample {sample.Asset.Id}.");
            if (sample.EncodedLength <= 0 || sample.EncodedLength % 16 != 0
                || sample.Provenance.Any(value => value.SourceRange.Length != sample.EncodedLength)
                || sample.DecodedFrameCount <= 0 || sample.LoopEndFrame != sample.DecodedFrameCount
                || sample.LoopStartFrame is < 0
                || sample.LoopStartFrame is { } loopStart && loopStart >= sample.LoopEndFrame
                || sample.IsLooping != sample.LoopStartFrame.HasValue)
                throw new InvalidDataException($"Sound sample {sample.Asset.Id} metadata is invalid.");
            if (sample.Provenance.All(value => value.Origin != ProjectSoundOrigin.Base))
                RequireAttached(sample.Asset, ProjectSoundSchema.SampleCanonicalFormatVersion,
                    assets, $"sound sample {sample.Asset.Id}");
        }

        var programs = new Dictionary<AssetId, ProjectSoundProgram>();
        var nativePrograms = new Dictionary<int, ProjectSoundProgram>();
        var sourcePrograms = new HashSet<(string Game, int Level, string Bank, int NativeIndex)>();
        foreach (var program in state.Programs)
        {
            ValidateProvenance(program.Provenance, "sound program");
            if (!sourcePrograms.Add((program.Provenance.Game, program.Provenance.Level,
                    program.Provenance.BankSha256, program.Provenance.NativeIndex)))
                throw new InvalidDataException(
                    $"Project contains duplicate source sound program {program.Provenance.NativeIndex}.");
            if (program.Provenance.Origin == ProjectSoundOrigin.Base)
                nativePrograms.Add(program.Provenance.NativeIndex, program);
            if (program.Operations is null
                || program.Operations.Count > ProjectSoundSchema.MaximumOperationsPerProgram
                || program.Operations.Any(value => value is null)
                || program.PreviewSupported != (program.Asset is not null)
                || program.PreviewSupported == (program.PreviewBlocker is not null))
                throw new InvalidDataException(
                    $"Sound program {program.Provenance.NativeIndex} metadata is invalid.");
            if (program.Asset is null)
            {
                if (program.SourceContentId is not null)
                    throw new InvalidDataException(
                        $"Unsupported sound program {program.Provenance.NativeIndex} has a canonical ID.");
            }
            else
            {
                ValidateReference(program.Asset, AssetKind.Sound, "sound program");
                ValidateContentId(program.SourceContentId, $"sound program {program.Asset.Id}");
                if (program.Asset.Id != ProjectSounds.ProgramId(program.SourceContentId!))
                    throw new InvalidDataException(
                        $"Sound program {program.Provenance.NativeIndex} canonical ID is invalid.");
                if (!programs.TryAdd(program.Asset.Id, program)
                    && !EquivalentProgramAsset(programs[program.Asset.Id], program))
                    throw new InvalidDataException(
                        $"Sound asset {program.Asset.Id} has conflicting native program definitions.");
            }
        }

        foreach (var program in state.Programs)
        {
            var owner = program.Asset?.Id.ToString() ?? $"native program {program.Provenance.NativeIndex}";
            foreach (var operation in program.Operations)
            {
                ValidateRange(operation.SourceRange,
                    $"sound program {owner} operation {operation.NativeIndex}");
                if (operation.NativeIndex < 0)
                    throw new InvalidDataException($"Sound program {owner} has an invalid operation index.");
                if (operation.NativeReferencedProgramIndex is < 0
                    || operation.ReferencedProgram is not null
                    && operation.NativeReferencedProgramIndex is null)
                    throw new InvalidDataException(
                        $"Sound program {owner} operation {operation.NativeIndex} has invalid native dependency provenance.");
                var parameterLength = operation.Type switch
                {
                    1 or 9 => 24,
                    2 or 3 or 4 => 16,
                    5 or 6 or 7 or 8 => 32,
                    _ => 0,
                };
                if (operation.ParameterBytes is not null
                    && operation.ParameterBytes.Length != parameterLength
                    || operation.ParameterBytes is null
                    && program.Provenance.Origin != ProjectSoundOrigin.Base)
                    throw new InvalidDataException(
                        $"Sound program {owner} operation {operation.NativeIndex} parameter data is invalid.");
                if (operation.Tone is { } tone)
                {
                    var field = $"sound.program[{program.Provenance.NativeIndex}].operation[{operation.NativeIndex}].sample";
                    ValidateReference(tone.Sample, AssetKind.SoundSample, field);
                    if (!samples.ContainsKey(tone.Sample.Id))
                        throw new InvalidDataException(
                            $"Sound owner {owner} field {field} is missing sample {tone.Sample.Id}.");
                }
                if (operation.ReferencedProgram is { } target)
                {
                    var field = $"sound.program[{program.Provenance.NativeIndex}].operation[{operation.NativeIndex}].program";
                    ValidateReference(target, AssetKind.Sound, field);
                    if (!programs.ContainsKey(target.Id))
                        throw new InvalidDataException(
                            $"Sound owner {owner} field {field} is missing program {target.Id}.");
                }
            }
        }
        ValidateProgramCycles(programs);

        var remapSlots = 0;
        if (state.MobyRemaps.Select(value => value.DefinitionIndex).Distinct().Count()
                != state.MobyRemaps.Count
            || state.MobyRemaps.Select(value => value.ClassId).Distinct().Count()
                != state.MobyRemaps.Count)
            throw new InvalidDataException("Project contains duplicate moby sound-map definitions.");
        foreach (var map in state.MobyRemaps)
        {
            if (map.DefinitionIndex < 0 || map.ClassId < 0 || map.Slots is null
                || map.Slots.Count > 0 && map.RawListOffset <= 0
                || map.Slots.Any(value => value is null))
                throw new InvalidDataException($"Moby sound map 0x{map.ClassId:X4} is invalid.");
            ValidateRange(map.ConfigurationRange, $"moby sound map 0x{map.ClassId:X4}");
            if (map.Moby is not null) ValidateReference(map.Moby, AssetKind.Moby, "moby sound-map owner");
            remapSlots = checked(remapSlots + map.Slots.Count);
            ValidateRemapSlots(map.Slots, programs, nativePrograms, $"moby 0x{map.ClassId:X4}");
        }
        remapSlots = checked(remapSlots + state.GlobalRemaps.Count);
        if (remapSlots > ProjectSoundSchema.MaximumRemapSlots)
            throw new InvalidDataException("Project sound remap has too many slots.");
        ValidateRemapSlots(state.GlobalRemaps, programs, nativePrograms, "global sound remap");
    }

    private static void ValidateRemapSlots(
        IReadOnlyList<ProjectSoundRemapSlot> slots,
        IReadOnlyDictionary<AssetId, ProjectSoundProgram> programs,
        IReadOnlyDictionary<int, ProjectSoundProgram> nativePrograms,
        string owner)
    {
        if (slots.Select(value => value.Slot).Distinct().Count() != slots.Count)
            throw new InvalidDataException($"{owner} contains duplicate sound-remap slots.");
        foreach (var slot in slots)
        {
            ValidateRange(slot.SourceRange, $"{owner} slot {slot.Slot}");
            if (slot.Slot < 0 || slot.NativeProgramIndex < -1
                || slot.NativeProgramIndex == -1 && (slot.Sound is not null || slot.Blocker is not null)
                || slot.NativeProgramIndex >= 0 && slot.Sound is null && string.IsNullOrWhiteSpace(slot.Blocker)
                || slot.Sound is not null && slot.Blocker is not null)
                throw new InvalidDataException($"{owner} slot {slot.Slot} metadata is invalid.");
            if (slot.NativeProgramIndex >= 0 && slot.Sound is null
                && !nativePrograms.ContainsKey(slot.NativeProgramIndex))
                throw new InvalidDataException(
                    $"Sound owner {owner} slot {slot.Slot} native index {slot.NativeProgramIndex} "
                    + "does not resolve an unsupported base program.");
            if (slot.Sound is null) continue;
            ValidateReference(slot.Sound, AssetKind.Sound, $"{owner} slot {slot.Slot}");
            if (!programs.ContainsKey(slot.Sound.Id))
                throw new InvalidDataException(
                    $"Sound owner {owner} slot {slot.Slot} is missing program {slot.Sound.Id} "
                    + $"(native index {slot.NativeProgramIndex}).");
        }
    }

    private static void ValidateProgramCycles(IReadOnlyDictionary<AssetId, ProjectSoundProgram> programs)
    {
        var visiting = new HashSet<AssetId>();
        var visited = new HashSet<AssetId>();
        foreach (var id in programs.Keys) Visit(id);
        return;

        void Visit(AssetId id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id))
                throw new InvalidDataException($"Sound program dependency cycle reaches {id}.");
            var program = programs[id];
            foreach (var operation in program.Operations.Where(value => value.ReferencedProgram is not null))
            {
                var target = operation.ReferencedProgram!;
                if (visiting.Contains(target.Id))
                    throw new InvalidDataException(
                        $"Sound owner {id} operation {operation.NativeIndex} creates a recursive edge to {target.Id}.");
                Visit(target.Id);
            }
            visiting.Remove(id);
            visited.Add(id);
        }
    }

    private static bool EquivalentProgramAsset(ProjectSoundProgram left, ProjectSoundProgram right) =>
        left.SourceContentId == right.SourceContentId
        && left.Volume == right.Volume
        && left.VolumeGroup == right.VolumeGroup
        && left.Pan == right.Pan
        && left.InstanceLimit == right.InstanceLimit
        && left.Flags == right.Flags
        && left.PreviewSupported == right.PreviewSupported
        && left.PreviewBlocker == right.PreviewBlocker
        && left.Operations.Count == right.Operations.Count
        && left.Operations.Zip(right.Operations).All(pair =>
            pair.First.Type == pair.Second.Type
            && pair.First.Delay == pair.Second.Delay
            && EquivalentParameterBytes(pair.First, pair.Second)
            && pair.First.Tone == pair.Second.Tone
            && pair.First.ReferencedProgram == pair.Second.ReferencedProgram);

    private static bool EquivalentParameterBytes(
        ProjectSoundOperation left,
        ProjectSoundOperation right)
    {
        var leftBytes = (left.ParameterBytes ?? []).ToArray();
        var rightBytes = (right.ParameterBytes ?? []).ToArray();
        if (leftBytes.Length != rightBytes.Length) return false;
        var offset = left.Type switch
        {
            1 or 9 => 0x10,
            2 or 3 => 4,
            5 or 6 or 8 => 0x0c,
            _ => -1,
        };
        if (offset >= 0 && leftBytes.Length >= offset + sizeof(int))
        {
            leftBytes.AsSpan(offset, sizeof(int)).Clear();
            rightBytes.AsSpan(offset, sizeof(int)).Clear();
        }
        return leftBytes.SequenceEqual(rightBytes);
    }

    private static void ValidateProvenance(ProjectSoundProvenance value, string owner)
    {
        if (value is null || !Enum.IsDefined(value.Origin) || value.Game is not "UYA"
            || value.Level < 0 || value.NativeIndex < 0
            || value.BankSha256.Length != 64
            || value.BankSha256.Any(character => !char.IsAsciiHexDigitLower(character)))
            throw new InvalidDataException($"{owner} provenance is invalid.");
        ValidateRange(value.SourceRange, $"{owner} provenance");
    }

    private static void ValidateContentId(string? value, string owner)
    {
        if (value is null || value.Length != AssetId.TextLength
            || value.Any(character => !char.IsAsciiHexDigitLower(character)))
            throw new InvalidDataException($"{owner} source content ID is invalid.");
    }

    private static void ValidateRange(ProjectSoundSourceRange? value, string owner, bool nullable = false)
    {
        if (value is null)
        {
            if (nullable) return;
            throw new InvalidDataException($"{owner} source range is missing.");
        }
        if (value.Offset < 0 || value.Length < 0)
            throw new InvalidDataException($"{owner} source range is invalid.");
    }

    private static void ValidateReference(ProjectAssetReference value, AssetKind kind, string owner)
    {
        if (value is null || value.Kind != kind || value.Id.ToString().Length != AssetId.TextLength)
            throw new InvalidDataException($"{owner} has a wrong-kind or invalid asset reference.");
    }

    private static void RequireAttached(
        ProjectAssetReference reference,
        uint formatVersion,
        IReadOnlyList<ProjectAttachedAsset> assets,
        string owner)
    {
        var asset = assets.SingleOrDefault(value => value.Id == reference.Id);
        if (asset is null || asset.Kind != reference.Kind || asset.CanonicalFormatVersion != formatVersion)
            throw new InvalidDataException($"{owner} requires its encoded attached blob.");
    }
}

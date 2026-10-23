namespace Forge.Host.Domain;

public static class ProjectSoundSchema
{
    public const int CurrentVersion = 1;
    public const uint ProgramCanonicalFormatVersion = 1;
    public const uint SampleCanonicalFormatVersion = 1;
    public const int MaximumPrograms = 32_767;
    public const int MaximumSamples = 32_767;
    public const int MaximumOperationsPerProgram = 127;
    public const int MaximumRemapSlots = 32_767;
}

public static class SoundBankAssetSchema
{
    public const uint CanonicalFormatVersion = 1;
}

public enum ProjectSoundOrigin : byte
{
    Base = 1,
    Imported = 2,
    Custom = 3,
}

public sealed record ProjectSoundSourceRange(int Offset, int Length);

public sealed record ProjectSoundProvenance(
    ProjectSoundOrigin Origin,
    string Game,
    int Level,
    string BankSha256,
    int NativeIndex,
    ProjectSoundSourceRange SourceRange);

public sealed record ProjectSoundTone(
    sbyte Priority,
    sbyte Volume,
    sbyte CenterNote,
    sbyte CenterFine,
    short Pan,
    sbyte NoteLow,
    sbyte NoteHigh,
    sbyte PitchBendLow,
    sbyte PitchBendHigh,
    ushort Adsr1,
    ushort Adsr2,
    ushort Flags,
    uint Reserved,
    ProjectAssetReference Sample);

public sealed record ProjectSoundOperation(
    int NativeIndex,
    byte Type,
    sbyte Argument0,
    sbyte Argument1,
    sbyte Argument2,
    int Delay,
    ProjectSoundSourceRange SourceRange,
    ProjectSoundTone? Tone,
    int? NativeReferencedProgramIndex,
    ProjectAssetReference? ReferencedProgram,
    byte[]? ParameterBytes = null);

public sealed record ProjectSoundProgram(
    ProjectAssetReference? Asset,
    string? SourceContentId,
    ProjectSoundProvenance Provenance,
    sbyte Volume,
    sbyte VolumeGroup,
    short Pan,
    sbyte InstanceLimit,
    ushort Flags,
    IReadOnlyList<ProjectSoundOperation> Operations,
    bool PreviewSupported,
    string? PreviewBlocker);

public sealed record ProjectSoundSample(
    ProjectAssetReference Asset,
    string SourceContentId,
    IReadOnlyList<ProjectSoundProvenance> Provenance,
    int EncodedLength,
    int DecodedFrameCount,
    bool IsLooping,
    int? LoopStartFrame,
    int LoopEndFrame);

public sealed record ProjectSoundRemapSlot(
    int Slot,
    int NativeProgramIndex,
    ProjectAssetReference? Sound,
    ProjectSoundSourceRange SourceRange,
    string? Blocker);

public sealed record ProjectMobySoundMap(
    int DefinitionIndex,
    int ClassId,
    ProjectAssetReference? Moby,
    short RawListOffset,
    ProjectSoundSourceRange ConfigurationRange,
    IReadOnlyList<ProjectSoundRemapSlot> Slots);

public sealed record ProjectSoundState(
    int SchemaVersion,
    IReadOnlyList<ProjectSoundProgram> Programs,
    IReadOnlyList<ProjectSoundSample> Samples,
    IReadOnlyList<ProjectMobySoundMap> MobyRemaps,
    IReadOnlyList<ProjectSoundRemapSlot> GlobalRemaps,
    int OpaqueOtherRecordCount,
    ProjectSoundSourceRange? OpaqueOtherRange,
    ProjectSoundSourceRange? SourceRemapRange)
{
    public static ProjectSoundState Empty { get; } = new(
        ProjectSoundSchema.CurrentVersion, [], [], [], [], 0, null, null);
}

public sealed record ProjectAssetReferenceEdge(
    ProjectAssetReference Owner,
    ProjectReference Reference,
    string OwnerField);

internal static class ProjectSounds
{
    public static AssetId ProgramId(string sourceContentId) => AssetId.Compute(
        AssetKind.Sound,
        ProjectSoundSchema.ProgramCanonicalFormatVersion,
        Convert.FromHexString(sourceContentId));

    public static AssetId SampleId(ReadOnlySpan<byte> encodedBytes) => AssetId.Compute(
        AssetKind.SoundSample,
        ProjectSoundSchema.SampleCanonicalFormatVersion,
        encodedBytes);

    public static IEnumerable<ProjectAssetReferenceEdge> References(ProjectSoundState state)
    {
        foreach (var program in state.Programs.Where(value => value.Asset is not null))
        {
            foreach (var operation in program.Operations)
            {
                if (operation.Tone is { } tone)
                    yield return Edge(program.Asset!, tone.Sample,
                        $"sound.program[{program.Provenance.NativeIndex}].operation[{operation.NativeIndex}].sample");
                if (operation.ReferencedProgram is { } referenced)
                    yield return Edge(program.Asset!, referenced,
                        $"sound.program[{program.Provenance.NativeIndex}].operation[{operation.NativeIndex}].program");
            }
        }
        foreach (var map in state.MobyRemaps.Where(value => value.Moby is not null))
            foreach (var slot in map.Slots.Where(value => value.Sound is not null))
                yield return Edge(map.Moby!, slot.Sound!,
                    $"sound.moby[0x{map.ClassId:X4}].slot[{slot.Slot}]");
    }

    public static IEnumerable<AssetId> AssetIds(ProjectSoundState state) =>
        state.Programs.Where(value => value.Asset is not null).Select(value => value.Asset!.Id)
            .Concat(state.Samples.Select(value => value.Asset.Id))
            .Concat(state.Programs.SelectMany(value => value.Operations)
                .SelectMany(value => new[] { value.Tone?.Sample, value.ReferencedProgram })
                .Where(value => value is not null).Select(value => value!.Id))
            .Concat(state.MobyRemaps.Where(value => value.Moby is not null).Select(value => value.Moby!.Id))
            .Concat(state.MobyRemaps.SelectMany(value => value.Slots)
                .Where(value => value.Sound is not null).Select(value => value.Sound!.Id))
            .Concat(state.GlobalRemaps.Where(value => value.Sound is not null).Select(value => value.Sound!.Id));

    private static ProjectAssetReferenceEdge Edge(
        ProjectAssetReference owner,
        ProjectAssetReference target,
        string field) => new(owner, new(
            ProjectReferenceDomain.Asset,
            field,
            false,
            AssetKind: target.Kind,
            AssetId: target.Id), field);
}

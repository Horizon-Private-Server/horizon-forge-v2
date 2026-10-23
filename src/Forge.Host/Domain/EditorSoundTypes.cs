namespace Forge.Host.Domain;

public sealed record EditorSoundSample(
    string AssetId,
    string SourceContentId,
    int EncodedLength,
    int DecodedFrameCount,
    bool IsLooping,
    int? LoopStartFrame,
    int LoopEndFrame,
    int? SampleRate,
    int Channels);

public sealed record EditorSoundMobyDependent(
    int ClassId,
    string? MobyAssetId,
    IReadOnlyList<int> RemapSlots);

public sealed record EditorSoundProgram(
    string? AssetId,
    string? SourceContentId,
    int NativeIndex,
    string Origin,
    string SourceGame,
    int SourceLevel,
    string BankSha256,
    string? ImportBatchId,
    int VolumeGroup,
    IReadOnlyList<int> GlobalRemapSlots,
    IReadOnlyList<EditorSoundMobyDependent> MobyDependents,
    IReadOnlyList<EditorSoundSample> Samples,
    bool HasRandomTriggerGate,
    bool PreviewSupported,
    string? Diagnostic);

public sealed record EditorSoundWorkspace(
    IReadOnlyList<EditorSoundProgram> Programs,
    int SampleCount,
    int MobyRemapCount,
    int GlobalRemapCount);

public sealed record EditorSoundPreview(
    int NativeIndex,
    string AssetId,
    string SampleId,
    int SampleRate,
    int Channels,
    byte[] Pcm16,
    bool IsLooping,
    int? LoopStartFrame,
    int LoopEndFrame,
    float Gain,
    int PanDegrees,
    bool IsVariable);

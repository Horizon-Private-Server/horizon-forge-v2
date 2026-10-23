using Forge.Host.Domain;

namespace Forge.Host.Bridge;

internal static class SoundPayloadCodec
{
    private const int MaximumPrograms = ProjectSoundSchema.MaximumPrograms;
    private const int MaximumPcmBytes = 8_000_000;

    public static SoundPreviewRequest DecodePreviewRequest(byte[] payload)
    {
        var reader = new PayloadReader(payload);
        var nativeIndex = reader.ReadInt32();
        var preferAudible = reader.ReadBoolean();
        var catalogRootPath = Optional(reader.ReadString());
        var bankAssetId = Optional(reader.ReadString());
        var game = Optional(reader.ReadString());
        reader.Complete();
        if (nativeIndex < 0 || nativeIndex >= MaximumPrograms)
            PayloadFormat.Malformed("Sound program index is invalid");
        if ((catalogRootPath is null || bankAssetId is null || game is null)
            && (catalogRootPath is not null || bankAssetId is not null || game is not null))
            PayloadFormat.Malformed("Catalog sound preview source is incomplete");
        if (bankAssetId is not null)
        {
            try { _ = AssetId.Parse(bankAssetId); }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                _ = exception;
                PayloadFormat.Malformed("Catalog sound bank Asset ID is invalid");
            }
        }
        return new(nativeIndex, preferAudible, catalogRootPath, bankAssetId, game);
    }

    public static byte[] EncodeWorkspace(EditorSoundWorkspace value)
    {
        if (value.Programs.Count > MaximumPrograms) PayloadFormat.Malformed("Sound program list exceeds limit");
        var writer = new PayloadWriter();
        writer.WriteUInt32((uint)value.Programs.Count);
        foreach (var program in value.Programs)
        {
            writer.WriteString(program.AssetId ?? string.Empty);
            writer.WriteString(program.SourceContentId ?? string.Empty);
            writer.WriteInt32(program.NativeIndex);
            writer.WriteString(program.Origin);
            writer.WriteString(program.SourceGame);
            writer.WriteInt32(program.SourceLevel);
            writer.WriteString(program.BankSha256);
            writer.WriteString(program.ImportBatchId ?? string.Empty);
            writer.WriteInt32(program.VolumeGroup);
            WriteInts(writer, program.GlobalRemapSlots);
            writer.WriteUInt32((uint)program.MobyDependents.Count);
            foreach (var dependent in program.MobyDependents)
            {
                writer.WriteInt32(dependent.ClassId);
                writer.WriteString(dependent.MobyAssetId ?? string.Empty);
                WriteInts(writer, dependent.RemapSlots);
            }
            writer.WriteUInt32((uint)program.Samples.Count);
            foreach (var sample in program.Samples)
            {
                writer.WriteString(sample.AssetId);
                writer.WriteString(sample.SourceContentId);
                writer.WriteInt32(sample.EncodedLength);
                writer.WriteInt32(sample.DecodedFrameCount);
                writer.WriteBoolean(sample.IsLooping);
                writer.WriteInt32(sample.LoopStartFrame ?? -1);
                writer.WriteInt32(sample.LoopEndFrame);
                writer.WriteInt32(sample.SampleRate ?? 0);
                writer.WriteInt32(sample.Channels);
            }
            writer.WriteBoolean(program.HasRandomTriggerGate);
            writer.WriteBoolean(program.PreviewSupported);
            writer.WriteString(program.Diagnostic ?? string.Empty);
        }
        writer.WriteInt32(value.SampleCount);
        writer.WriteInt32(value.MobyRemapCount);
        writer.WriteInt32(value.GlobalRemapCount);
        return writer.ToArray();
    }

    public static byte[] EncodePreview(EditorSoundPreview value)
    {
        var writer = new PayloadWriter();
        writer.WriteInt32(value.NativeIndex);
        writer.WriteString(value.AssetId);
        writer.WriteString(value.SampleId);
        writer.WriteInt32(value.SampleRate);
        writer.WriteInt32(value.Channels);
        writer.WriteBytes(value.Pcm16, MaximumPcmBytes);
        writer.WriteBoolean(value.IsLooping);
        writer.WriteInt32(value.LoopStartFrame ?? -1);
        writer.WriteInt32(value.LoopEndFrame);
        writer.WriteSingle(value.Gain);
        writer.WriteInt32(value.PanDegrees);
        writer.WriteBoolean(value.IsVariable);
        return writer.ToArray();
    }

    private static void WriteInts(PayloadWriter writer, IReadOnlyList<int> values)
    {
        if (values.Count > MaximumPrograms) PayloadFormat.Malformed("Sound dependency list exceeds limit");
        writer.WriteUInt32((uint)values.Count);
        foreach (var value in values) writer.WriteInt32(value);
    }

    private static string? Optional(string value) => value.Length == 0 ? null : value;
}

internal readonly record struct SoundPreviewRequest(
    int NativeIndex,
    bool PreferAudible,
    string? CatalogRootPath,
    string? BankAssetId,
    string? Game);

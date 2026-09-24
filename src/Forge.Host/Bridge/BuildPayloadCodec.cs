namespace Forge.Host.Bridge;

public static class BuildPayloadCodec
{
    public static byte[] EncodeRequest(UyaBuildPatchRequestPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.ProjectRoot);
        writer.WriteString(value.CatalogRoot);
        writer.WriteString(value.CleanSourceIso);
        writer.WriteString(value.DevelopmentIso);
        writer.WriteString(value.SourceFingerprint);
        writer.WriteStrings(value.AcknowledgedWarnings);
        writer.WriteBoolean(value.ForceFullImage);
        writer.WriteStrings(value.IncludedLayers);
        return writer.ToArray();
    }

    public static UyaBuildPatchRequestPayload DecodeRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new UyaBuildPatchRequestPayload(
            reader.ReadString(),
            reader.ReadString(),
            reader.ReadString(),
            reader.ReadString(),
            reader.ReadString(),
            reader.ReadStrings(),
            reader.ReadBoolean(),
            reader.ReadStrings());
        reader.Complete();
        return value;
    }

    public static byte[] EncodePlanRequest(UyaBuildPlanRequestPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.ProjectRoot);
        writer.WriteString(value.CatalogRoot);
        return writer.ToArray();
    }

    public static UyaBuildPlanRequestPayload DecodePlanRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new UyaBuildPlanRequestPayload(reader.ReadString(), reader.ReadString());
        reader.Complete();
        return value;
    }

    public static byte[] EncodePlan(UyaBuildPlanPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteUInt32(checked((uint)value.Layers.Count));
        foreach (var layer in value.Layers)
        {
            writer.WriteString(layer.Layer);
            writer.WriteString(layer.State);
            writer.WriteBoolean(layer.CanDefer);
        }
        return writer.ToArray();
    }

    public static UyaBuildPlanPayload DecodePlan(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var count = reader.ReadUInt32();
        var layers = new UyaBuildLayerStatusPayload[count];
        for (var index = 0; index < layers.Length; index++)
            layers[index] = new(reader.ReadString(), reader.ReadString(), reader.ReadBoolean());
        reader.Complete();
        return new(layers);
    }

    public static byte[] EncodeProgress(UyaBuildPatchProgressPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.Phase);
        writer.WriteUInt64(value.Completed);
        writer.WriteUInt64(value.Total);
        writer.WriteString(value.Message);
        return writer.ToArray();
    }

    public static UyaBuildPatchProgressPayload DecodeProgress(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new UyaBuildPatchProgressPayload(
            reader.ReadString(), reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadString());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeResult(UyaBuildPatchResultPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteBoolean(value.Succeeded);
        writer.WriteBoolean(value.RequiresWarningAcknowledgement);
        writer.WriteStrings(value.WarningCodes);
        writer.WriteStrings(value.Diagnostics);
        writer.WriteString(value.Message);
        writer.WriteString(value.NextAction);
        writer.WriteString(value.DevelopmentIsoPath);
        writer.WriteString(value.PatchMode);
        writer.WriteString(value.OutputLevelWadSha256);
        writer.WriteUInt32(value.BakedLayerCount);
        writer.WriteBoolean(value.BakeWasCurrent);
        return writer.ToArray();
    }

    public static UyaBuildPatchResultPayload DecodeResult(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new UyaBuildPatchResultPayload(
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadStrings(),
            reader.ReadStrings(),
            reader.ReadString(),
            reader.ReadString(),
            reader.ReadString(),
            reader.ReadString(),
            reader.ReadString(),
            reader.ReadUInt32(),
            reader.ReadBoolean());
        reader.Complete();
        return value;
    }
}

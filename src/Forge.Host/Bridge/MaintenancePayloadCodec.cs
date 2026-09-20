namespace Forge.Host.Bridge;

public static partial class BridgePayloadCodec
{
    public static byte[] EncodeProjectAssetRepairRequest(ProjectAssetRepairRequestPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.ProjectPath);
        writer.WriteString(value.CatalogRootPath);
        writer.WriteString(value.SourceIsoPath);
        return writer.ToArray();
    }

    public static ProjectAssetRepairRequestPayload DecodeProjectAssetRepairRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new ProjectAssetRepairRequestPayload(reader.ReadString(), reader.ReadString(), reader.ReadString());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeCatalogMaintenanceRequest(CatalogMaintenanceRequestPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.CatalogRootPath);
        writer.WriteStrings(value.ProjectRoots);
        return writer.ToArray();
    }

    public static CatalogMaintenanceRequestPayload DecodeCatalogMaintenanceRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new CatalogMaintenanceRequestPayload(reader.ReadString(), reader.ReadStrings());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeCatalogCollectionRequest(CatalogCollectionRequestPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.CatalogRootPath);
        writer.WriteStrings(value.ProjectRoots);
        writer.WriteString(value.ConfirmationToken);
        return writer.ToArray();
    }

    public static CatalogCollectionRequestPayload DecodeCatalogCollectionRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new CatalogCollectionRequestPayload(reader.ReadString(), reader.ReadStrings(), reader.ReadString());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeCatalogMaintenance(CatalogMaintenancePayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteUInt32(value.ProjectCount);
        writer.WriteUInt32(value.CatalogAssetCount);
        writer.WriteUInt32(value.ProtectedAssetCount);
        writer.WriteUInt32(value.CandidateCount);
        writer.WriteUInt32(value.CatalogCandidateCount);
        writer.WriteUInt64(value.CandidateBytes);
        writer.WriteString(value.ConfirmationToken);
        writer.WriteStrings(value.CandidateKinds);
        writer.WriteStrings(value.Blockers);
        return writer.ToArray();
    }

    public static CatalogMaintenancePayload DecodeCatalogMaintenance(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new CatalogMaintenancePayload(
            reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt64(),
            reader.ReadString(), reader.ReadStrings(), reader.ReadStrings());
        reader.Complete();
        return value;
    }
}

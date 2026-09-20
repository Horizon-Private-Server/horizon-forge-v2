using System.Buffers.Binary;
using static Forge.Host.Bridge.PayloadFormat;

namespace Forge.Host.Bridge;

public static partial class BridgePayloadCodec
{
    public const uint MaxEchoDelayMs = 60_000;

    public static byte[] EncodeHandshake(HostHandshake handshake)
    {
        var writer = new PayloadWriter();
        writer.WriteString(handshake.HostVersion);
        writer.WriteString(handshake.SdkRevision);
        writer.WriteStrings(handshake.SupportedGames);
        writer.WriteStrings(handshake.Capabilities);
        return writer.ToArray();
    }

    public static HostHandshake DecodeHandshake(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new HostHandshake(
            reader.ReadString(),
            reader.ReadString(),
            reader.ReadStrings(),
            reader.ReadStrings());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeEchoRequest(string message, uint delayMs = 0)
    {
        if (delayMs > MaxEchoDelayMs) Malformed("Echo delay exceeds limit");
        var writer = new PayloadWriter();
        writer.WriteUInt32(delayMs);
        writer.WriteString(message);
        return writer.ToArray();
    }

    public static EchoRequest DecodeEchoRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var delayMs = reader.ReadUInt32();
        if (delayMs > MaxEchoDelayMs) Malformed("Echo delay exceeds limit");
        var value = new EchoRequest(reader.ReadString(), delayMs);
        reader.Complete();
        return value;
    }

    public static byte[] EncodeText(string value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value);
        return writer.ToArray();
    }

    public static string DecodeText(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = reader.ReadString();
        reader.Complete();
        return value;
    }

    public static byte[] EncodeProgress(uint completed, uint total)
    {
        if (total == 0 || completed > total) Malformed("Invalid progress values");
        var payload = GC.AllocateUninitializedArray<byte>(8);
        BinaryPrimitives.WriteUInt32LittleEndian(payload, completed);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), total);
        return payload;
    }

    public static BridgeProgress DecodeProgress(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 8) Malformed("Progress payload must contain eight bytes");
        var completed = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var total = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        if (total == 0 || completed > total) Malformed("Invalid progress values");
        return new(completed, total);
    }

    public static byte[] EncodeUyaIsoValidation(UyaIsoValidationPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteBoolean(value.IsSupported);
        writer.WriteString(value.Game);
        writer.WriteString(value.Region);
        writer.WriteString(value.Revision);
        writer.WriteString(value.Serial);
        writer.WriteUInt64(value.Size);
        writer.WriteString(value.Fingerprint);
        writer.WriteString(value.Diagnostic);
        return writer.ToArray();
    }

    public static UyaIsoValidationPayload DecodeUyaIsoValidation(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new UyaIsoValidationPayload(
            reader.ReadBoolean(), reader.ReadString(), reader.ReadString(), reader.ReadString(),
            reader.ReadString(), reader.ReadUInt64(), reader.ReadString(), reader.ReadString());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeDevelopmentIsoRequest(DevelopmentIsoRequest value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.SourcePath);
        writer.WriteString(value.TargetPath);
        writer.WriteString(value.Fingerprint);
        writer.WriteBoolean(value.Overwrite);
        return writer.ToArray();
    }

    public static DevelopmentIsoRequest DecodeDevelopmentIsoRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new DevelopmentIsoRequest(
            reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadBoolean());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeDevelopmentIso(DevelopmentIsoPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.Path);
        writer.WriteUInt64(value.Size);
        writer.WriteString(value.Fingerprint);
        return writer.ToArray();
    }

    public static DevelopmentIsoPayload DecodeDevelopmentIso(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new DevelopmentIsoPayload(reader.ReadString(), reader.ReadUInt64(), reader.ReadString());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeUyaAssetImportRequest(UyaAssetImportRequestPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.SourceIsoPath);
        writer.WriteString(value.CatalogRootPath);
        writer.WriteString(value.Fingerprint);
        writer.WriteString(value.Revision);
        writer.WriteBoolean(value.Force);
        return writer.ToArray();
    }

    public static UyaAssetImportRequestPayload DecodeUyaAssetImportRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new UyaAssetImportRequestPayload(
            reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadBoolean());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeUyaAssetImportResult(UyaAssetImportResultPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteUInt32(value.CompletedLevels);
        writer.WriteUInt32(value.TotalLevels);
        writer.WriteUInt32(value.AssetAppearances);
        writer.WriteUInt32(value.UniqueAssets);
        writer.WriteUInt32(value.FailedAssets);
        writer.WriteBoolean(value.Resumed);
        return writer.ToArray();
    }

    public static UyaAssetImportResultPayload DecodeUyaAssetImportResult(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new UyaAssetImportResultPayload(
            reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadBoolean());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeUyaProjectOptions(UyaProjectOptionsPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteUInt32s(value.Levels);
        writer.WriteStrings(value.Warnings);
        return writer.ToArray();
    }

    public static UyaProjectOptionsPayload DecodeUyaProjectOptions(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new UyaProjectOptionsPayload(reader.ReadUInt32s(), reader.ReadStrings());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeUyaProjectCreationRequest(UyaProjectCreationRequestPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.SourceIsoPath);
        writer.WriteString(value.CatalogRootPath);
        writer.WriteString(value.ProjectPath);
        writer.WriteString(value.Name);
        writer.WriteString(value.Fingerprint);
        writer.WriteString(value.Revision);
        writer.WriteUInt32(value.Level);
        writer.WriteBoolean(value.AllowPartial);
        return writer.ToArray();
    }

    public static UyaProjectCreationRequestPayload DecodeUyaProjectCreationRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new UyaProjectCreationRequestPayload(
            reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadString(),
            reader.ReadString(), reader.ReadString(), reader.ReadUInt32(), reader.ReadBoolean());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeUyaProjectPreflightRequest(UyaProjectPreflightRequestPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.SourceIsoPath);
        writer.WriteString(value.CatalogRootPath);
        writer.WriteUInt32(value.Level);
        return writer.ToArray();
    }

    public static UyaProjectPreflightRequestPayload DecodeUyaProjectPreflightRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new UyaProjectPreflightRequestPayload(reader.ReadString(), reader.ReadString(), reader.ReadUInt32());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeUyaProjectPreflight(UyaProjectPreflightPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteUInt32(value.Level);
        writer.WriteUInt32(value.SourceInstanceCount);
        writer.WriteUInt32(value.RenderableInstanceCount);
        writer.WriteUInt32(value.ModelLessInstanceCount);
        writer.WriteUInt32(value.MissingAssetInstanceCount);
        writer.WriteUInt32(value.MissingClassCount);
        writer.WriteStrings(value.Warnings);
        return writer.ToArray();
    }

    public static UyaProjectPreflightPayload DecodeUyaProjectPreflight(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new UyaProjectPreflightPayload(
            reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(),
            reader.ReadUInt32(), reader.ReadStrings());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeProjectInspectRequest(ProjectInspectRequestPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.ProjectPath);
        writer.WriteString(value.CatalogRootPath);
        return writer.ToArray();
    }

    public static ProjectInspectRequestPayload DecodeProjectInspectRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new ProjectInspectRequestPayload(reader.ReadString(), reader.ReadString());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeProjectRenameRequest(ProjectRenameRequestPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.ProjectPath);
        writer.WriteString(value.CatalogRootPath);
        writer.WriteString(value.Name);
        return writer.ToArray();
    }

    public static ProjectRenameRequestPayload DecodeProjectRenameRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new ProjectRenameRequestPayload(reader.ReadString(), reader.ReadString(), reader.ReadString());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeProjectRecoveryRequest(ProjectRecoveryRequestPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.ProjectPath);
        writer.WriteString(value.CatalogRootPath);
        writer.WriteString(value.RecoveryId);
        return writer.ToArray();
    }

    public static ProjectRecoveryRequestPayload DecodeProjectRecoveryRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var value = new ProjectRecoveryRequestPayload(reader.ReadString(), reader.ReadString(), reader.ReadString());
        reader.Complete();
        return value;
    }

    public static byte[] EncodeForgeProjectDescriptor(ForgeProjectDescriptorPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.Path);
        writer.WriteString(value.Name);
        writer.WriteString(value.TargetGame);
        writer.WriteString(value.TargetRegion);
        writer.WriteString(value.TargetRevision);
        writer.WriteString(value.BakeProfile);
        writer.WriteUInt32(value.BaseLevel);
        writer.WriteUInt64(value.ModifiedUnixMilliseconds);
        writer.WriteUInt32(value.EntityCount);
        writer.WriteUInt32(value.MissingAssetCount);
        writer.WriteBoolean(value.IsDirty);
        writer.WriteBoolean(value.MigrationPending);
        writer.WriteStrings(value.Warnings);
        if (value.Recoveries.Count > MaxListItems) Malformed("List exceeds item limit");
        writer.WriteUInt32((uint)value.Recoveries.Count);
        foreach (var recovery in value.Recoveries)
        {
            writer.WriteString(recovery.Id);
            writer.WriteUInt64(recovery.CreatedUnixMilliseconds);
            writer.WriteString(recovery.Name);
            writer.WriteUInt32(recovery.EntityCount);
            writer.WriteString(recovery.Fingerprint);
            writer.WriteUInt64(recovery.Size);
        }
        if (value.MissingAssets.Count > 4_096) Malformed("Missing-asset list exceeds item limit");
        writer.WriteUInt32((uint)value.MissingAssets.Count);
        foreach (var missing in value.MissingAssets)
        {
            writer.WriteString(missing.Id);
            writer.WriteString(missing.Kind);
            writer.WriteUInt32(missing.EntityCount);
            writer.WriteBoolean(missing.Repairable);
            writer.WriteStrings(missing.Provenance);
        }
        return writer.ToArray();
    }

    public static ForgeProjectDescriptorPayload DecodeForgeProjectDescriptor(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var path = reader.ReadString();
        var name = reader.ReadString();
        var targetGame = reader.ReadString();
        var targetRegion = reader.ReadString();
        var targetRevision = reader.ReadString();
        var bakeProfile = reader.ReadString();
        var baseLevel = reader.ReadUInt32();
        var modified = reader.ReadUInt64();
        var entityCount = reader.ReadUInt32();
        var missingAssetCount = reader.ReadUInt32();
        var isDirty = reader.ReadBoolean();
        var migrationPending = reader.ReadBoolean();
        var warnings = reader.ReadStrings();
        var recoveryCount = reader.ReadUInt32();
        if (recoveryCount > MaxListItems) Malformed("List exceeds item limit");
        var recoveries = new ProjectRecoverySnapshotPayload[recoveryCount];
        for (var index = 0; index < recoveries.Length; index++)
        {
            recoveries[index] = new(
                reader.ReadString(), reader.ReadUInt64(), reader.ReadString(), reader.ReadUInt32(), reader.ReadString(), reader.ReadUInt64());
        }
        var missingCount = reader.ReadUInt32();
        if (missingCount > 4_096) Malformed("Missing-asset list exceeds item limit");
        var missingAssets = new MissingProjectAssetPayload[missingCount];
        for (var index = 0; index < missingAssets.Length; index++)
        {
            missingAssets[index] = new(
                reader.ReadString(), reader.ReadString(), reader.ReadUInt32(), reader.ReadBoolean(), reader.ReadStrings());
        }
        var value = new ForgeProjectDescriptorPayload(
            path, name, targetGame, targetRegion, targetRevision, bakeProfile, baseLevel, modified,
            entityCount, missingAssetCount, isDirty, migrationPending, warnings, recoveries, missingAssets);
        reader.Complete();
        return value;
    }

}

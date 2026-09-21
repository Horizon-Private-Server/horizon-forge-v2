using Forge.Host.Domain;

namespace Forge.Host.Bridge;

internal static class ProjectBridgeHandlers
{
    public static async Task<byte[]> HandleAsync(
        BridgeFrame frame,
        string sdkRevision,
        Func<IsoProgress, ValueTask> progress,
        CancellationToken cancellationToken) => frame.Opcode switch
    {
        BridgeOpcode.ListUyaProjectLevels => await ListLevelsAsync(frame.Payload, cancellationToken),
        BridgeOpcode.CreateUyaProject => await CreateProjectAsync(frame.Payload, progress, cancellationToken),
        BridgeOpcode.PreflightUyaProject => await PreflightAsync(frame.Payload, cancellationToken),
        BridgeOpcode.InspectForgeProject => await InspectAsync(frame.Payload, cancellationToken),
        BridgeOpcode.RenameForgeProject => await RenameAsync(frame.Payload, cancellationToken),
        BridgeOpcode.RestoreForgeProjectRecovery => await RestoreAsync(frame.Payload, cancellationToken),
        BridgeOpcode.MigrateForgeProject => await MigrateAsync(frame.Payload, cancellationToken),
        BridgeOpcode.RepairForgeProjectAssets => await RepairAsync(
            frame.Payload, sdkRevision, progress, cancellationToken),
        BridgeOpcode.PreviewCatalogGarbageCollection => await PreviewCatalogAsync(frame.Payload, cancellationToken),
        BridgeOpcode.CollectCatalogGarbage => await CollectCatalogAsync(frame.Payload, cancellationToken),
        _ => throw new BridgeProtocolException(BridgeErrorCode.UnknownOpcode, $"Unsupported project opcode: {frame.Opcode}"),
    };

    private static async Task<byte[]> ListLevelsAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var result = await UyaProjectService.GetCreationOptionsAsync(
            BridgePayloadCodec.DecodeText(payload), cancellationToken);
        return BridgePayloadCodec.EncodeUyaProjectOptions(new(
            result.Levels.Select(level => checked((uint)level)).ToArray(), result.Warnings));
    }

    private static async Task<byte[]> CreateProjectAsync(
        byte[] payload,
        Func<IsoProgress, ValueTask> progress,
        CancellationToken cancellationToken)
    {
        var request = BridgePayloadCodec.DecodeUyaProjectCreationRequest(payload);
        var result = await UyaProjectService.CreateAsync(new(
            request.SourceIsoPath,
            request.CatalogRootPath,
            request.ProjectPath,
            request.Name,
            request.Fingerprint,
            request.Revision,
            checked((int)request.Level),
            request.AllowPartial), progress, cancellationToken);
        return EncodeProject(result);
    }

    private static async Task<byte[]> PreflightAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var request = BridgePayloadCodec.DecodeUyaProjectPreflightRequest(payload);
        var result = await UyaProjectService.PreflightAsync(
            request.SourceIsoPath, request.CatalogRootPath, checked((int)request.Level), cancellationToken);
        return BridgePayloadCodec.EncodeUyaProjectPreflight(new(
            checked((uint)result.Level),
            checked((uint)result.SourceInstanceCount),
            checked((uint)result.RenderableInstanceCount),
            checked((uint)result.ModelLessInstanceCount),
            checked((uint)result.MissingAssetInstanceCount),
            checked((uint)result.MissingClassCount),
            result.Warnings));
    }

    private static async Task<byte[]> InspectAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var request = BridgePayloadCodec.DecodeProjectInspectRequest(payload);
        var catalog = await AssetCatalogStore.OpenAsync(request.CatalogRootPath, cancellationToken);
        return EncodeProject(await UyaProjectService.InspectAsync(
            request.ProjectPath, catalog, cancellationToken: cancellationToken));
    }

    private static async Task<byte[]> RenameAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var request = BridgePayloadCodec.DecodeProjectRenameRequest(payload);
        var catalog = await AssetCatalogStore.OpenAsync(request.CatalogRootPath, cancellationToken);
        return EncodeProject(await UyaProjectService.RenameAsync(
            request.ProjectPath, request.Name, catalog, cancellationToken));
    }

    private static async Task<byte[]> RestoreAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var request = BridgePayloadCodec.DecodeProjectRecoveryRequest(payload);
        var catalog = await AssetCatalogStore.OpenAsync(request.CatalogRootPath, cancellationToken);
        return EncodeProject(await UyaProjectService.RestoreRecoveryAsync(
            request.ProjectPath, request.RecoveryId, catalog, cancellationToken));
    }

    private static async Task<byte[]> MigrateAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var request = BridgePayloadCodec.DecodeProjectAssetRepairRequest(payload);
        var catalog = await AssetCatalogStore.OpenAsync(request.CatalogRootPath, cancellationToken);
        return EncodeProject(await UyaProjectService.MigrateAsync(
            request.ProjectPath, catalog, request.SourceIsoPath, cancellationToken));
    }

    private static async Task<byte[]> RepairAsync(
        byte[] payload,
        string sdkRevision,
        Func<IsoProgress, ValueTask> progress,
        CancellationToken cancellationToken)
    {
        var request = BridgePayloadCodec.DecodeProjectAssetRepairRequest(payload);
        return EncodeProject(await UyaProjectService.RepairMissingAssetsAsync(
            request.ProjectPath,
            request.CatalogRootPath,
            request.SourceIsoPath,
            $"forge-uya-v1+{sdkRevision}",
            progress,
            cancellationToken));
    }

    private static async Task<byte[]> PreviewCatalogAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var request = BridgePayloadCodec.DecodeCatalogMaintenanceRequest(payload);
        return EncodeMaintenance(await AssetCatalogMaintenance.PreviewAsync(
            request.CatalogRootPath, request.ProjectRoots, cancellationToken));
    }

    private static async Task<byte[]> CollectCatalogAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var request = BridgePayloadCodec.DecodeCatalogCollectionRequest(payload);
        return EncodeMaintenance(await AssetCatalogMaintenance.CollectAsync(
            request.CatalogRootPath, request.ProjectRoots, request.ConfirmationToken, cancellationToken));
    }

    private static byte[] EncodeProject(ForgeProjectDescriptor result) =>
        BridgePayloadCodec.EncodeForgeProjectDescriptor(new(
            result.Path,
            result.Name,
            result.TargetGame,
            result.TargetRegion,
            result.TargetRevision,
            result.BakeProfile,
            checked((uint)result.BaseLevel),
            checked((ulong)result.ModifiedUnixMilliseconds),
            checked((uint)result.EntityCount),
            checked((uint)result.MissingAssetCount),
            result.IsDirty,
            result.MigrationPending,
            result.Warnings,
            result.Recoveries.Select(recovery => new ProjectRecoverySnapshotPayload(
                recovery.Id,
                checked((ulong)recovery.CreatedUnixMilliseconds),
                recovery.Name,
                checked((uint)recovery.EntityCount),
                recovery.Fingerprint,
                checked((ulong)recovery.Size))).ToArray(),
            result.MissingAssets.Select(missing => new MissingProjectAssetPayload(
                missing.Id.ToString(),
                missing.Kind.ToString(),
                checked((uint)missing.EntityCount),
                missing.Repairable,
                missing.Provenance)).ToArray()));

    private static byte[] EncodeMaintenance(AssetCatalogMaintenanceReport result) =>
        BridgePayloadCodec.EncodeCatalogMaintenance(new(
            checked((uint)result.ProjectCount),
            checked((uint)result.CatalogAssetCount),
            checked((uint)result.ProtectedAssetCount),
            checked((uint)result.Candidates.Count),
            checked((uint)result.Candidates.Count(candidate => candidate.Cataloged)),
            checked((ulong)result.Candidates.Sum(candidate => candidate.Size)),
            result.ConfirmationToken,
            result.Candidates.GroupBy(candidate => candidate.Kind?.ToString() ?? "Orphan")
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key}: {group.Count()}").ToArray(),
            result.Blockers));
}

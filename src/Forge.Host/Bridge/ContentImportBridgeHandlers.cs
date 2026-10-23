using Forge.Host.Domain;
using Forge.Host.Games.UYA;

namespace Forge.Host.Bridge;

internal static class ContentImportBridgeHandlers
{
    public static byte[] Capabilities(byte[] payload)
    {
        var request = BridgePayloadCodec.DecodeContentImportCapabilityRequest(payload);
        if (request.TargetGame != "UYA")
            throw new ArgumentException($"Content import is not defined for target game {request.TargetGame}.");
        var inventory = request.Inventory.Select(value =>
        {
            if (value.State > byte.MaxValue
                || !Enum.IsDefined(typeof(ContentImportSourceState), (byte)value.State))
                throw new ArgumentException($"{value.Family} has an invalid source state.");
            return new ContentImportFamilyInventory(
                value.Family,
                (ContentImportSourceState)value.State,
                value.Diagnostic);
        }).ToArray();
        var matrix = UyaContentImportCapabilities.Create(request.TargetRegion, request.TargetRevision, inventory);
        return BridgePayloadCodec.EncodeContentImportCapabilityMatrix(new(
            checked((uint)matrix.SchemaVersion),
            matrix.Game,
            matrix.Region,
            matrix.Revision,
            matrix.Families.Select(value => new ContentImportFamilyCapabilityPayload(
                value.Family,
                value.Label,
                (uint)value.Mode,
                (uint)value.SourceState,
                value.ImplementationReady,
                value.Selectable,
                value.DefaultSelectedWhenReady,
                value.DefaultSelected,
                value.DisabledReason,
                value.Ownership,
                value.Dependencies,
                value.ConflictNamespace,
                value.NativeSemantics,
                value.ProtectedSurvivorPolicy,
                value.NoEditIdentity,
                value.Limits,
                value.Evidence)).ToArray()));
    }

    public static async Task<byte[]> PlanAsync(
        byte[] payload,
        Func<IsoProgress, ValueTask> progress,
        CancellationToken cancellationToken,
        EditorRuntime? runtime = null)
    {
        var request = BridgePayloadCodec.DecodeContentImportPlanRequest(payload);
        var planRequest = new UyaContentImportPlanRequest(
            request.ProjectPath,
            request.CatalogRootPath,
            request.SourceIsoPath,
            request.SourceFingerprint,
            checked((int)request.SourceLevel),
            Selections(request.Selections));
        var plan = runtime is null
            ? await UyaContentImportPlanningService.PlanAsync(planRequest, progress, cancellationToken)
            : (await runtime.ReadWorkspaceAsync(request.ProjectPath, async (active, token) =>
                await UyaContentImportPlanningService.PrepareAsync(
                    planRequest, active.CreateProjection(), progress, token), cancellationToken)).Plan;
        return EncodePlan(plan);
    }

    public static async Task<byte[]> ApplyAsync(
        byte[] payload,
        EditorRuntime runtime,
        MobyDexCatalog mobyDex,
        Func<IsoProgress, ValueTask> progress,
        CancellationToken cancellationToken)
    {
        var request = BridgePayloadCodec.DecodeContentImportApplyRequest(payload);
        var snapshot = await UyaContentImportApplyService.ApplyAsync(runtime, new(
            request.ProjectPath,
            request.CatalogRootPath,
            request.SourceIsoPath,
            request.SourceFingerprint,
            checked((int)request.SourceLevel),
            request.BatchId,
            Selections(request.Selections),
            request.ReferenceChoices.Select(value => new UyaMobyExternalReferenceChoice(
                value.OwnerSourceIndex,
                value.FieldKey,
                value.SourceValue,
                value.Action <= byte.MaxValue
                    && Enum.IsDefined(typeof(UyaMobyExternalReferenceAction), (byte)value.Action)
                        ? (UyaMobyExternalReferenceAction)value.Action
                        : throw new ArgumentException("An import reference choice has an invalid action."),
                value.TargetEntityId is null ? null : EntityId.Parse(value.TargetEntityId))).ToArray()),
            mobyDex, progress, cancellationToken);
        return EditorPayloadCodec.EncodeSnapshot(snapshot);
    }

    public static async Task<byte[]> PlanRemovalAsync(
        byte[] payload,
        EditorRuntime runtime,
        CancellationToken cancellationToken)
    {
        var request = BridgePayloadCodec.DecodeContentImportRemovalPlanRequest(payload);
        var plan = await runtime.ReadWorkspaceAsync(request.ProjectPath, (workspace, token) =>
            Task.FromResult(UyaContentImportRemovalService.Prepare(
                workspace.CreateProjection(), request.BatchId, token).Plan), cancellationToken);
        return EncodeRemovalPlan(plan);
    }

    public static async Task<byte[]> ApplyRemovalAsync(
        byte[] payload,
        EditorRuntime runtime,
        CancellationToken cancellationToken)
    {
        var request = BridgePayloadCodec.DecodeContentImportRemovalApplyRequest(payload);
        var snapshot = await UyaContentImportRemovalService.ApplyAsync(
            runtime, request.ProjectPath, request.BatchId, request.TargetFingerprint, cancellationToken);
        return EditorPayloadCodec.EncodeSnapshot(snapshot);
    }

    private static ContentImportSelection[] Selections(
        IReadOnlyList<ContentImportSelectionPayload> values) => values.Select(value => new ContentImportSelection(
            value.Family,
            value.Mode <= byte.MaxValue && Enum.IsDefined(typeof(ContentImportMode), (byte)value.Mode)
                ? (ContentImportMode)value.Mode
                : throw new ArgumentException($"{value.Family} has an invalid import mode."))).ToArray();

    private static byte[] EncodePlan(ContentImportPlan plan) =>
        BridgePayloadCodec.EncodeContentImportPlan(new(
            checked((uint)plan.SchemaVersion),
            checked((uint)plan.CapabilitySchemaVersion),
            plan.BatchId,
            plan.Game,
            plan.Region,
            plan.Revision,
            plan.SourceFingerprint,
            plan.SourceLevelHash,
            checked((uint)plan.SourceLevel),
            plan.TargetProjectId,
            plan.TargetFingerprint,
            checked((uint)plan.TargetLevel),
            checked((ulong)plan.EstimatedBytes),
            plan.Families.Select(value => new ContentImportFamilyPlanPayload(
                value.Family,
                value.Label,
                (uint)value.SupportedMode,
                (uint)value.Mode,
                (uint)value.SourceState,
                value.Selected,
                value.DefaultSelected,
                value.Selectable,
                value.Add,
                value.Reuse,
                value.Remap,
                value.Remove,
                value.Protect,
                value.DependencyReasons,
                value.Conflicts,
                checked((ulong)value.EstimatedBytes),
                value.Warnings,
                value.Blockers)).ToArray(),
            plan.Warnings,
            plan.Blockers,
            (plan.ReferenceRequirements ?? []).Select(value => new ContentImportReferenceRequirementPayload(
                value.Family,
                value.OwnerSourceIndex,
                value.FieldKey,
                value.SourceValue,
                (uint)value.TargetKind,
                value.Nullable,
                value.CandidateEntityIds.Select(candidate => candidate.ToString()).ToArray())).ToArray()));

    private static byte[] EncodeRemovalPlan(ContentImportRemovalPlan plan) =>
        BridgePayloadCodec.EncodeContentImportRemovalPlan(new(
            checked((uint)plan.SchemaVersion),
            plan.BatchId,
            plan.TargetFingerprint,
            plan.Delete,
            plan.Survivors.Select(value => new ContentImportRemovalSurvivorPayload(
                value.StableId, value.Reason)).ToArray(),
            plan.Shared,
            plan.Protected,
            plan.Warnings));
}

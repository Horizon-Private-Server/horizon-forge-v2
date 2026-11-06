using Forge.Host.Domain;
using RatchetPs2.Core.Games;

namespace Forge.Host.Games.UYA;

internal sealed record UyaConvertedContentImportPlanRequest(
    GameId SourceGame,
    string ProjectPath,
    string CatalogRootPath,
    string SourceIsoPath,
    string SourceFingerprint,
    int SourceLevel,
    IReadOnlyList<ContentImportSelection> Selections);

internal static class UyaConvertedContentImportService
{
    public static async Task<ContentImportPlan> PlanAsync(
        UyaConvertedContentImportPlanRequest request,
        ForgeProjectWorkspace workspace,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(request, workspace);
        var profile = UyaContentImportDonorProfile.For(request.SourceGame);
        var source = await ContentImportSourceCache.ReadAsync(request.SourceGame, GameId.UYA, request.SourceIsoPath, request.SourceFingerprint,
            request.SourceLevel, "NTSC-U", profile.Revision, progress, cancellationToken);
        var prepared = await UyaConvertedContentImportPreparation.CreateAsync(request, source,
            new MobyDexCatalog(UyaMobyDexDataset.Create()), progress, cancellationToken);
        var plan = CreatePlan(request, workspace, source, prepared.Source, prepared.Diagnostics, cancellationToken, prepared.Warnings);
        if (progress is not null) await progress(new(10_000, 10_000));
        return plan;
    }

    internal static ContentImportPlan CreatePlan(
        UyaConvertedContentImportPlanRequest request,
        ForgeProjectWorkspace workspace,
        ContentImportSource source,
        UyaBaseLevelData native,
        IReadOnlyDictionary<string, string>? diagnostics = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? warnings = null)
    {
        Validate(request, workspace);
        var profile = UyaContentImportDonorProfile.For(request.SourceGame);
        ContentImportSourceCache.ValidateIdentity(source.Identity, request.SourceGame, "NTSC-U", profile.Revision, request.SourceFingerprint);
        var inventories = UyaContentImportPlanningService.Inventory(native).Select(family =>
            diagnostics?.GetValueOrDefault(family.Family) is { } diagnostic
                ? family with { State = ContentImportSourceState.Malformed, Diagnostic = diagnostic }
                : family).ToArray();
        var capabilities = UyaContentImportCapabilities.Create("NTSC-U", "1.00", inventories.Select(family =>
            new ContentImportFamilyInventory(family.Family, family.State, family.Diagnostic)).ToArray());
        capabilities = capabilities with
        {
            Families = capabilities.Families.Select(family => family.Mode == ContentImportMode.Unsupported
                ? family : family with { Mode = ContentImportMode.Replace }).ToArray(),
        };
        var plan = UyaContentImportPlanningService.CreatePlan(new(
            source.Identity.Fingerprint.ToLowerInvariant(), source.LevelHash, request.SourceLevel,
            workspace.Manifest.ProjectId.ToString(), workspace.CurrentFingerprint,
            workspace.Manifest.BaseLevel.Level, inventories, workspace.Content, request.Selections,
            Warnings: warnings, SourceGame: request.SourceGame.ToString(), SourceRegion: source.Identity.Region, SourceRevision: source.Identity.Revision,
            Capabilities: capabilities, ReplacesSceneSettings: native.HasSceneSettings, SourceCatalogRevision: native.CatalogRevision), cancellationToken);
        plan = UyaContentImportPlanningService.AddExternalReferenceRequirements(plan,
            UyaMobyContentImportService.ExternalReferenceRequirements(native), workspace.Content);
        if (native.HasSceneSettings) plan = UyaContentImportPlanningService.AddLevelSettingsReferenceRequirements(plan, native.LevelSettings, native.Entities, workspace.Content);
        plan = UyaContentImportPlanningService.AddAreaReferenceRequirements(plan, native.Entities, workspace.Content);
        return UyaContentImportPlanningService.AddDirectionalLightBlockers(plan, native.Entities, workspace.Content);
    }

    internal static void Validate(UyaConvertedContentImportPlanRequest request, ForgeProjectWorkspace workspace)
    {
        var profile = UyaContentImportDonorProfile.For(request.SourceGame);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CatalogRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceIsoPath);
        if (request.SourceFingerprint.Length != 32 || !request.SourceFingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException($"The {request.SourceGame} donor fingerprint must be a 32-character MD5 value.");
        if (request.SourceLevel < 0 || request.SourceLevel > profile.MaximumLevel) throw new ArgumentOutOfRangeException(nameof(request.SourceLevel));
        if (workspace.Manifest.Target is not { Game: "UYA", Region: "NTSC-U", Revision: "1.00" })
            throw new InvalidDataException($"{request.SourceGame} content imports require a UYA NTSC-U 1.00 target project.");
    }
    public static async Task<EditorSnapshot> ApplyAsync(
        EditorRuntime runtime, UyaConvertedContentImportPlanRequest request, string batchId,
        IReadOnlyList<ContentImportReferenceChoice> referenceChoices, MobyDexCatalog mobyDex,
        Func<IsoProgress, ValueTask>? progress, CancellationToken cancellationToken)
    {
        var command = await runtime.ReadWorkspaceAsync(request.ProjectPath, async (active, token) =>
        {
            var workspace = active.CreateProjection();
            UyaConvertedContentImportService.Validate(request, workspace);
            var profile = UyaContentImportDonorProfile.For(request.SourceGame);
            var source = await ContentImportSourceCache.ReadAsync(request.SourceGame, GameId.UYA, request.SourceIsoPath, request.SourceFingerprint,
                request.SourceLevel, "NTSC-U", profile.Revision, progress, token);
            var prepared = await UyaConvertedContentImportPreparation.CreateAsync(request, source, mobyDex, progress, token);
            return await PrepareCommitAsync(request, workspace, source, prepared, batchId, referenceChoices, mobyDex, token);
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (progress is not null) await progress(new(9_900, 10_000));
        return await runtime.CommitContentImportAsync(command, cancellationToken);
    }

    internal static async Task<ContentImportBatchCommit> PrepareCommitAsync(
        UyaConvertedContentImportPlanRequest request, ForgeProjectWorkspace workspace, ContentImportSource source,
        UyaConvertedContentImportPreparation prepared, string batchId,
        IReadOnlyList<ContentImportReferenceChoice> referenceChoices, MobyDexCatalog mobyDex,
        CancellationToken cancellationToken = default)
    {
        var plan = UyaConvertedContentImportService.CreatePlan(request, workspace, source,
            prepared.Source, prepared.Diagnostics, cancellationToken, prepared.Warnings);
        if (plan.Families.Any(family => family.Selected && family.Mode != ContentImportMode.Replace))
            throw new InvalidOperationException($"{request.SourceGame} content importing supports replacement selections only.");
        var command = await UyaContentImportApplyService.PrepareCommitAsync(new(workspace, prepared.Catalog, prepared.Source, plan),
            batchId, mobyDex, referenceChoices, cancellationToken);
        // Keep the untouched donor gameplay tables, including PVars not owned by a current instance.
        if (plan.Families.Any(family => family is { Family: "mobys", Selected: true }))
        {
            var raw = new ProjectAssetAttachment(AssetKind.World, 1, prepared.RawGameplay);
            var id = AssetId.Compute(raw.Kind, raw.CanonicalFormatVersion, raw.CanonicalBytes.Span);
            var existing = workspace.Content.Assets.Any(asset => asset.Id == id);
            command = command with
            {
                Attachments = existing ? command.Attachments : command.Attachments.Append(raw).ToArray(),
                Batch = command.Batch with
                {
                    SourcePayload = new(id, raw.Kind),
                    Added = (existing ? command.Batch.Added : command.Batch.Added.Append($"asset:{id}"))
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    Reused = (existing ? command.Batch.Reused.Append($"asset:{id}") : command.Batch.Reused)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal).ToArray(),
                },
            };
        }
        var imported = command.Batch.Added.Where(value => value.StartsWith("entity:", StringComparison.Ordinal))
            .Select(value => EntityId.Parse(value["entity:".Length..])).ToHashSet();
        return command with
        {
            Content = command.Content with
            {
                Entities = command.Content.Entities.Select(entity => imported.Contains(entity.EntityId)
                    && entity.Provenance is { } provenance ? entity with { Provenance = provenance with { Game = request.SourceGame.ToString() } } : entity).ToArray(),
            },
        };
    }
}

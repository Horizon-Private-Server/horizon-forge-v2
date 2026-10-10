using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal static class UyaProjectOpenHydrationService
{
    public static async Task<EditorDiagnostic?> HydrateAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore? catalog,
        MobyDexCatalog mobyDex,
        CancellationToken cancellationToken)
    {
        var pvars = await UyaMobyPVarService.HydrateFromSourceAsync(workspace, mobyDex, cancellationToken);
        var collision = catalog is null
            ? null
            : await HydrateCollisionLinksAsync(workspace, catalog, cancellationToken);
        if (pvars is null) return collision;
        if (collision is null) return pvars;
        return new(
            "project.source-hydrated",
            pvars.Severity >= collision.Severity ? pvars.Severity : collision.Severity,
            $"{pvars.Message} {collision.Message}");
    }

    private static async Task<EditorDiagnostic?> HydrateCollisionLinksAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        CancellationToken cancellationToken)
    {
        if (workspace.Manifest.Target.Game != "UYA"
            || workspace.Content.Entities.Where(entity => entity.Collision?.Kind == ProjectCollisionPieceKind.Solid)
                .All(entity => entity.Collision!.LinkedInstanceEntityIds is not null))
            return null;
        try
        {
            var entities = workspace.Content.Entities.ToList();
            var payloads = await UyaBaseLayerStore.ReadCollisionPayloadsAsync(
                workspace, catalog, cancellationToken);
            if (!UyaInstancedCollisionLinkRecoveryService.Recover(entities, payloads, catalog))
                return new(
                    "collision.links-source-hydration-failed",
                    EditorDiagnosticSeverity.Warning,
                    "Source collision links could not be refreshed; existing links were preserved.");
            var links = entities.Where(entity =>
                    entity.Collision?.Kind == ProjectCollisionPieceKind.Solid).ToDictionary(
                entity => entity.EntityId,
                entity => entity.Collision!.LinkedInstanceEntityIds ?? (IReadOnlyList<EntityId>)[]);
            var changed = workspace.ReplaceCollisionLinks(links);
            var linked = links.Values.Count(value => value.Count > 0);
            return changed == 0 ? null : new(
                "collision.links-source-hydrated",
                EditorDiagnosticSeverity.Info,
                $"Refreshed source collision links for {changed} collision component(s)"
                + $" ({linked} linked). Save the project to persist them.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            return new(
                "collision.links-source-hydration-failed",
                EditorDiagnosticSeverity.Warning,
                $"Source collision links could not be refreshed ({exception.Message}).");
        }
    }
}

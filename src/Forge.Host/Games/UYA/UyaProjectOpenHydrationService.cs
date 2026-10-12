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
        var diagnostics = new List<EditorDiagnostic>();
        Add(diagnostics, await UyaMobyPVarService.HydrateFromSourceAsync(workspace, mobyDex, cancellationToken));
        Add(diagnostics, await UyaTfragProjectService.HydrateAsync(workspace, catalog, cancellationToken));
        if (catalog is not null)
            Add(diagnostics, await HydrateCollisionLinksAsync(workspace, catalog, cancellationToken));
        if (diagnostics.Count == 0) return null;
        return new(
            "project.source-hydrated",
            diagnostics.Max(value => value.Severity),
            string.Join(' ', diagnostics.Select(value => value.Message)));
    }

    private static void Add(ICollection<EditorDiagnostic> diagnostics, EditorDiagnostic? diagnostic)
    {
        if (diagnostic is not null) diagnostics.Add(diagnostic);
    }

    private static async Task<EditorDiagnostic?> HydrateCollisionLinksAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        CancellationToken cancellationToken)
    {
        if (workspace.Manifest.Target.Game != "UYA") return null;
        var missingLinks = workspace.Content.Entities
            .Where(entity => entity.Collision?.Kind == ProjectCollisionPieceKind.Solid)
            .Any(entity => entity.Collision!.LinkedInstanceEntityIds is null);
        if (!missingLinks) return null;
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
                + $" ({linked} linked). Save the project to persist the refresh.");
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

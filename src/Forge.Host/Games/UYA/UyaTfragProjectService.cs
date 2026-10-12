using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Tfrags;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaTfragProjectService
{
    public static UyaTfragProjectImport Create(
        IReadOnlyList<UyaBaseLayerPayload> baseLayers,
        int level,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseLayers);
        var payloads = baseLayers.Where(value => value.Layer == BakeLayerId.Tfrags)
            .Select(value => new TfragPiecePayload(
                value.SourceArchive,
                value.SourceIndex == 0 ? TfragPieceSourceKind.Primary : TfragPieceSourceKind.Chunk,
                value.SourceIndex,
                value.Bytes))
            .ToArray();
        if (payloads.Length == 0)
            throw new InvalidDataException("The verified base level contains no tfrag source payloads.");

        var extraction = TfragPieceConverter.Extract(payloads, GameId.UYA, cancellationToken);
        var assets = extraction.Assets.Select(asset =>
        {
            if (asset.CanonicalFormatVersion != ProjectTfragSchema.CanonicalFormatVersion)
                throw new InvalidDataException(
                    $"SDK tfrag canonical version {asset.CanonicalFormatVersion} is unsupported.");
            var attachment = new ProjectAssetAttachment(
                AssetKind.Tfrag, asset.CanonicalFormatVersion, asset.CanonicalBytes);
            return new
            {
                asset.ContentId,
                Attachment = attachment,
                Reference = new ProjectAssetReference(
                    AssetId.Compute(attachment.Kind, attachment.CanonicalFormatVersion, attachment.CanonicalBytes.Span),
                    AssetKind.Tfrag),
                asset.Geometry,
            };
        }).ToArray();
        var byContentId = assets.ToDictionary(value => value.ContentId, StringComparer.Ordinal);
        var entities = extraction.Occurrences.Select(occurrence =>
        {
            var asset = byContentId[occurrence.ContentId];
            var kind = ProjectKind(occurrence.Provenance.SourceKind);
            var owner = kind == ProjectTfragSourceKind.Primary
                ? "Primary"
                : $"Chunk {occurrence.Provenance.SourceIndex}";
            return new ProjectEntity(
                EntityId.New(),
                $"Tfrag {owner} #{occurrence.Provenance.GroupIndex}",
                "tfrags",
                new(
                    Vector(occurrence.Transform.Position),
                    Quaternion(occurrence.Transform.Rotation),
                    Vector(occurrence.Transform.Scale)),
                asset.Reference,
                new("UYA", level, occurrence.Provenance.SourceArchive, occurrence.Provenance.GroupIndex),
                new(),
                Tfrag: new(
                    occurrence.OccurrenceKey,
                    occurrence.Provenance.SourceArchive,
                    kind,
                    occurrence.Provenance.SourceIndex,
                    occurrence.Provenance.GroupIndex,
                    occurrence.Provenance.OcclusionIndex,
                    occurrence.Provenance.OcclusionIndexStash,
                    asset.Geometry.TextureIds.ToArray(),
                    Vector(occurrence.Transform.Position),
                    new(
                        Vector(asset.Geometry.Bounds.Minimum),
                        Vector(asset.Geometry.Bounds.Maximum),
                        asset.Geometry.Bounds.Radius),
                    new(
                        occurrence.Diagnostics.SourceDataLength,
                        occurrence.Diagnostics.SourceVertexCount,
                        occurrence.Diagnostics.SourceTriangleCount,
                        occurrence.Diagnostics.DecodedVertexCount,
                        occurrence.Diagnostics.DecodedTriangleCount)));
        }).ToArray();
        var state = new ProjectTfragState(
            ProjectTfragSchema.CurrentVersion,
            extraction.Sources.Select(source => new ProjectTfragSource(
                source.SourceArchive,
                ProjectKind(source.SourceKind),
                source.SourceIndex,
                source.GroupCount)).ToArray());
        return new(state, entities, assets.Select(value => value.Attachment).ToArray());
    }

    public static Task ApplyAsync(
        ForgeProjectWorkspace workspace,
        UyaTfragProjectImport import,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(import);
        return workspace.ImportTfragsAsync(import.State, import.Entities, import.Assets, cancellationToken);
    }

    public static async Task<EditorDiagnostic?> HydrateAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore? catalog,
        CancellationToken cancellationToken)
    {
        if (workspace.Manifest.Target.Game != "UYA")
            return null;
        var importRequired = workspace.Content.Tfrags is null;
        var metadataRequired = workspace.Content.Entities.Any(entity => entity.Tfrag is { } tfrag
            && (tfrag.SourcePosition is null || tfrag.Bounds is null || tfrag.Diagnostics is null));
        if (!importRequired && !metadataRequired) return null;
        if (catalog is null)
            return MissingSource("the asset catalog is unavailable");

        try
        {
            var payloads = await UyaBaseLayerStore.ReadTfragPayloadsAsync(
                workspace, catalog, cancellationToken);
            var import = Create(payloads, workspace.Manifest.BaseLevel.Level, cancellationToken);
            if (importRequired)
            {
                await ApplyAsync(workspace, import, cancellationToken);
                return new(
                    "tfrags.source-hydrated",
                    EditorDiagnosticSeverity.Info,
                    $"Imported {import.Entities.Count} source tfrag piece(s). Save the project to persist them.");
            }
            var changed = workspace.RefreshTfragMetadata(import.Entities);
            return new(
                "tfrags.metadata-hydrated",
                EditorDiagnosticSeverity.Info,
                $"Refreshed source metadata for {changed} tfrag piece(s). Save the project to persist it.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            return MissingSource(exception.Message);
        }
    }

    private static EditorDiagnostic MissingSource(string reason) => new(
        "tfrags.source-hydration-required",
        EditorDiagnosticSeverity.Warning,
        $"Tfrag source data could not be refreshed because verified source/catalog data is required ({reason}); "
        + "existing terrain remains available.");

    private static ProjectTfragSourceKind ProjectKind(TfragPieceSourceKind kind) => kind switch
    {
        TfragPieceSourceKind.Primary => ProjectTfragSourceKind.Primary,
        TfragPieceSourceKind.Chunk => ProjectTfragSourceKind.Chunk,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown tfrag source kind."),
    };

    private static ProjectVector3 Vector(TfragPieceVector3 value) => new(value.X, value.Y, value.Z);

    private static ProjectQuaternion Quaternion(TfragPieceVector4 value) => new(value.X, value.Y, value.Z, value.W);
}

internal sealed record UyaTfragProjectImport(
    ProjectTfragState State,
    IReadOnlyList<ProjectEntity> Entities,
    IReadOnlyList<ProjectAssetAttachment> Assets);

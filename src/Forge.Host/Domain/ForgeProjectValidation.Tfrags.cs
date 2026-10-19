using RatchetPs2.Core.Tfrags;
using RatchetPs2.Sdk;

namespace Forge.Host.Domain;

internal static partial class ForgeProjectValidation
{
    private static void ValidateTfrags(
        ProjectTfragState? state,
        IReadOnlyList<ProjectEntity> entities,
        IReadOnlyList<ProjectAttachedAsset> assets)
    {
        var pieces = entities.Where(entity => entity.Tfrag is not null).ToArray();
        if (state is null)
        {
            if (pieces.Length > 0)
                throw new InvalidDataException("Project tfrag entities require import state.");
            return;
        }

        if (state.SchemaVersion != ProjectTfragSchema.CurrentVersion
            || state.Sources is null
            || state.CustomImports is null
            || state.CustomImports.Any(value => value is null)
            || state.Sources.Count > ProjectTfragSchema.MaximumSourceCount
            || state.CustomImports.Count > ProjectTfragSchema.MaximumCustomImports
            || state.Sources.Any(source => source is null))
            throw new InvalidDataException("Project tfrag import state is invalid.");
        if (state.Sources.Select(source => (source.SourceKind, source.SourceIndex)).Distinct().Count()
            != state.Sources.Count)
            throw new InvalidDataException("Project tfrag import state contains duplicate sources.");
        if (!state.Sources.SequenceEqual(state.Sources
                .OrderBy(source => source.SourceKind)
                .ThenBy(source => source.SourceIndex)
                .ThenBy(source => source.SourceArchive, StringComparer.Ordinal)))
            throw new InvalidDataException("Project tfrag sources are not deterministically ordered.");

        var totalGroups = 0L;
        foreach (var source in state.Sources)
        {
            if (!Enum.IsDefined(source.SourceKind)
                || source.SourceKind == ProjectTfragSourceKind.Primary && source.SourceIndex != 0
                || source.SourceKind == ProjectTfragSourceKind.Chunk && source.SourceIndex < 1
                || source.GroupCount < 0)
                throw new InvalidDataException("Project tfrag source ownership is invalid.");
            ValidateRelativeArchive(source.SourceArchive);
            totalGroups += source.GroupCount;
        }
        if (totalGroups > ProjectTfragSchema.MaximumGroupCount || pieces.Length != totalGroups)
            throw new InvalidDataException("Project tfrag occurrence count does not match its source inventory.");

        var attached = assets.ToDictionary(asset => asset.Id);
        if (state.CustomImports.Select(value => value.ImportId).Distinct(StringComparer.Ordinal).Count()
            != state.CustomImports.Count)
            throw new InvalidDataException("Project custom tfrag import IDs are duplicated.");
        var customById = state.CustomImports.ToDictionary(value => value.ImportId, StringComparer.Ordinal);
        var customPieceIds = new HashSet<EntityId>();
        foreach (var import in state.CustomImports)
        {
            if (!BakeSchema.IsFingerprint(import.ImportId))
                throw new InvalidDataException("Project custom tfrag import ID is invalid.");
            ValidateText(import.SourceName, "Custom tfrag source name");
            if (import.Source is not { Kind: AssetKind.StaticModelSource }
                || !attached.TryGetValue(import.Source.Id, out var sourceAsset)
                || sourceAsset.CanonicalFormatVersion != ProjectCustomTfragSchema.SourceFormatVersion
                || import.Dependencies is null || import.Textures is null || import.Pieces is null
                || import.Dependencies.Count > ProjectCustomModelSchema.MaximumDependencies
                || import.Textures.Count > ProjectTfragSchema.MaximumTextureDependencies
                || import.Pieces.Count == 0
                || import.Textures.Distinct().Count() != import.Textures.Count
                || import.Pieces.Any(value => !customPieceIds.Add(value)))
                throw new InvalidDataException("Project custom tfrag import state is invalid.");
            ValidateCustomModelDependencies(
                import.Dependencies, "Project custom tfrag dependencies are invalid.");
            foreach (var dependency in import.Dependencies)
                if (dependency.Asset is not { Kind: AssetKind.StaticModelSource }
                    || !attached.TryGetValue(dependency.Asset.Id, out var dependencyAsset)
                    || dependencyAsset.CanonicalFormatVersion != ProjectCustomTfragSchema.SourceFormatVersion)
                    throw new InvalidDataException("Project custom tfrag source dependency is invalid.");
            foreach (var texture in import.Textures)
                if (texture.Kind != AssetKind.Texture
                    || !attached.TryGetValue(texture.Id, out var textureAsset)
                    || textureAsset.CanonicalFormatVersion != ProjectTextureAssetSchema.CanonicalFormatVersion)
                    throw new InvalidDataException("Project custom tfrag texture is invalid.");
        }
        if (!customPieceIds.SetEquals(pieces.Where(value => value.Tfrag!.CustomImportId is not null)
                .Select(value => value.EntityId)))
            throw new InvalidDataException("Project custom tfrag piece inventory is inconsistent.");
        var sourcesByOwner = state.Sources.ToDictionary(source => (source.SourceKind, source.SourceIndex));
        var sourceGroups = pieces.GroupBy(entity =>
            (entity.Tfrag!.SourceKind, entity.Tfrag.SourceIndex)).ToDictionary(group => group.Key);
        foreach (var source in state.Sources)
        {
            var key = (source.SourceKind, source.SourceIndex);
            var groupIndexes = sourceGroups.GetValueOrDefault(key)?.Select(entity => entity.Tfrag!.GroupIndex)
                .Order().ToArray() ?? [];
            if (!groupIndexes.SequenceEqual(Enumerable.Range(0, source.GroupCount)))
                throw new InvalidDataException("Project tfrag source groups are incomplete or duplicated.");
        }
        if (pieces.Select(entity => entity.Tfrag!.OccurrenceKey)
                .Distinct(StringComparer.Ordinal).Count() != pieces.Length)
            throw new InvalidDataException("Project tfrag occurrence keys are duplicated.");

        foreach (var entity in pieces)
        {
            var piece = entity.Tfrag!;
            ValidateTfragTransform(entity.Transform, piece.Bounds);
            if (entity.Layer != "tfrags"
                || entity.Asset is not { Kind: AssetKind.Tfrag } asset
                || !attached.TryGetValue(asset.Id, out var attachedAsset)
                || attachedAsset.Kind != AssetKind.Tfrag
                || attachedAsset.CanonicalFormatVersion != ProjectTfragSchema.CanonicalFormatVersion)
                throw new InvalidDataException($"Tfrag entity {entity.EntityId} has an invalid canonical asset.");
            if (entity.Provenance is not { Game: "UYA" } provenance
                || provenance.Section != piece.SourceArchive
                || provenance.SourceIndex != piece.GroupIndex)
                throw new InvalidDataException($"Tfrag entity {entity.EntityId} provenance is inconsistent.");
            if (!BakeSchema.IsFingerprint(piece.OccurrenceKey))
                throw new InvalidDataException($"Tfrag entity {entity.EntityId} occurrence key is invalid.");
            ValidateRelativeArchive(piece.SourceArchive);
            if (!Enum.IsDefined(piece.SourceKind)
                || piece.SourceIndex < 0 || piece.GroupIndex < 0
                || !sourcesByOwner.TryGetValue((piece.SourceKind, piece.SourceIndex), out var source)
                || source.SourceArchive != piece.SourceArchive
                || piece.TextureIds is null
                || piece.TextureIds.Count > ProjectTfragSchema.MaximumTextureDependencies
                || piece.TextureIds.Any(textureId => textureId < 0)
                || !piece.TextureIds.SequenceEqual(piece.TextureIds.Distinct().Order())
                || piece.SourcePosition is { } sourcePosition && !ValidVector(sourcePosition)
                || piece.Bounds is { } bounds && (!ValidVector(bounds.Minimum)
                    || !ValidVector(bounds.Maximum) || !float.IsFinite(bounds.Radius) || bounds.Radius < 0
                    || bounds.Minimum.X > bounds.Maximum.X || bounds.Minimum.Y > bounds.Maximum.Y
                    || bounds.Minimum.Z > bounds.Maximum.Z)
                || piece.Diagnostics is { } diagnostics && new[]
                {
                    diagnostics.SourceDataLength,
                    diagnostics.SourceVertexCount,
                    diagnostics.SourceTriangleCount,
                    diagnostics.DecodedVertexCount,
                    diagnostics.DecodedTriangleCount,
                }.Any(value => value < 0))
                throw new InvalidDataException($"Tfrag entity {entity.EntityId} metadata is invalid.");
            if (piece.CustomImportId is { } customImportId)
            {
                if (piece.CustomSourceName is null
                    || !customById.TryGetValue(customImportId, out var customImport)
                    || customImport.SourceName != piece.CustomSourceName
                    || !customImport.Pieces.Contains(entity.EntityId))
                    throw new InvalidDataException($"Tfrag entity {entity.EntityId} custom import metadata is invalid.");
            }
            else if (piece.CustomSourceName is not null)
                throw new InvalidDataException($"Tfrag entity {entity.EntityId} has incomplete custom import metadata.");
            if (entity.State?.Disabled == true)
                throw new InvalidDataException($"Tfrag entity {entity.EntityId} cannot be disabled before native composition is available.");
        }
    }

    internal static void ValidateTfragTransform(ProjectTransform transform, ProjectTfragBounds? bounds)
    {
        var sdkTransform = new TfragPieceTransform(
            new(transform.Position.X, transform.Position.Y, transform.Position.Z),
            new(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W),
            new(transform.Scale.X, transform.Scale.Y, transform.Scale.Z));
        if (bounds is null)
        {
            TfragPieceConverter.ValidateTransform(sdkTransform);
            return;
        }
        TfragPieceConverter.ValidateTransform(
            new(
                new(bounds.Minimum.X, bounds.Minimum.Y, bounds.Minimum.Z),
                new(bounds.Maximum.X, bounds.Maximum.Y, bounds.Maximum.Z),
                bounds.Radius),
            sdkTransform);
    }

    private static void ValidateRelativeArchive(string value)
    {
        ValidateText(value, "Tfrag source archive");
        var normalized = value.Replace('\\', '/');
        if (normalized != value || normalized.StartsWith('/')
            || normalized.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..")
            || normalized.Split('/')[0].Contains(':'))
            throw new InvalidDataException("Tfrag source archive must be a normalized project-relative path.");
    }
}

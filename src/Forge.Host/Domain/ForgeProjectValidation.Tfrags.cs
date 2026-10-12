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
            || state.Sources.Count > ProjectTfragSchema.MaximumSourceCount
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

using RatchetPs2.Core.Gltf;

namespace Forge.Host.Domain;

internal sealed class ProjectAssetAttachmentCollector
{
    private readonly Dictionary<AssetId, ProjectAssetAttachment> _attachments = [];

    public IReadOnlyList<ProjectAssetAttachment> Attachments => _attachments
        .OrderBy(value => value.Key.ToString(), StringComparer.Ordinal)
        .Select(value => value.Value)
        .ToArray();

    public ProjectAssetReference Attach(
        AssetKind kind,
        uint canonicalFormatVersion,
        ReadOnlyMemory<byte> canonicalBytes,
        AssetId? parentId = null)
    {
        if (canonicalBytes.IsEmpty)
            throw new ArgumentException("Attached asset bytes cannot be empty.", nameof(canonicalBytes));
        var id = AssetId.Compute(kind, canonicalFormatVersion, canonicalBytes.Span);
        var attachment = new ProjectAssetAttachment(
            kind, canonicalFormatVersion, canonicalBytes, parentId);
        if (_attachments.TryGetValue(id, out var existing))
        {
            if (existing.Kind != attachment.Kind
                || existing.CanonicalFormatVersion != attachment.CanonicalFormatVersion
                || existing.ParentId != attachment.ParentId
                || !existing.CanonicalBytes.Span.SequenceEqual(attachment.CanonicalBytes.Span))
                throw new InvalidDataException($"Project asset {id} has conflicting import payloads.");
        }
        else
        {
            _attachments.Add(id, attachment);
        }
        return new(id, kind);
    }

    public (ProjectAssetReference Source, IReadOnlyList<ProjectCustomModelDependency> Dependencies)
        AttachStaticModelSources(
            ProjectCustomModelImportRequest request,
            StaticModelImportProvenance provenance,
            uint sourceFormatVersion)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(provenance);
        var source = Attach(AssetKind.StaticModelSource, sourceFormatVersion, request.SourceBytes);
        var fileByUri = request.Dependencies.ToDictionary(value => value.Uri, StringComparer.Ordinal);
        var dependencies = provenance.Dependencies.Select(dependency =>
        {
            var bytes = fileByUri[dependency.Uri].Bytes;
            return new ProjectCustomModelDependency(
                dependency.Uri,
                dependency.Kind == StaticModelDependencyKind.Buffer
                    ? ProjectCustomModelDependencyKind.Buffer
                    : ProjectCustomModelDependencyKind.Image,
                Attach(AssetKind.StaticModelSource, sourceFormatVersion, bytes));
        }).ToArray();
        return (source, dependencies);
    }
}

namespace Forge.Host.Domain;

public sealed partial class ForgeProjectWorkspace
{
    internal async Task CommitAssetsAsync(
        ForgeProjectContent content,
        IReadOnlyList<ProjectAssetAttachment> attachments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!SameJson(content.Assets, Content.Assets))
            throw new InvalidOperationException("The attachment transaction must retain existing asset metadata.");
        ArgumentNullException.ThrowIfNull(attachments);
        cancellationToken.ThrowIfCancellationRequested();

        var resolved = attachments.Select(attachment =>
        {
            ArgumentNullException.ThrowIfNull(attachment);
            return (Asset: CreateAttachedAsset(
                    attachment.Kind,
                    attachment.CanonicalFormatVersion,
                    attachment.CanonicalBytes,
                    attachment.ParentId,
                    nameof(attachments),
                    nameof(attachments)),
                Bytes: attachment.CanonicalBytes);
        }).ToArray();
        var unique = new List<(ProjectAttachedAsset Asset, ReadOnlyMemory<byte> Bytes)>();
        foreach (var group in resolved.GroupBy(value => value.Asset.Id))
        {
            var first = group.First();
            if (group.Any(value => value.Asset != first.Asset
                    || !value.Bytes.Span.SequenceEqual(first.Bytes.Span)))
                throw new InvalidDataException($"Project asset {group.Key} has inconsistent transaction data.");
            unique.Add(first);
        }

        var existing = Content.Assets.ToDictionary(asset => asset.Id);
        foreach (var value in unique)
            if (existing.TryGetValue(value.Asset.Id, out var current) && current != value.Asset)
                throw new InvalidDataException($"Project asset {value.Asset.Id} has inconsistent metadata.");
        var added = unique.Where(value => !existing.ContainsKey(value.Asset.Id)).Select(value => value.Asset);
        content = content with
        {
            Assets = Content.Assets.Concat(added)
                .OrderBy(asset => asset.Id.ToString(), StringComparer.Ordinal).ToArray(),
        };
        ValidateAttachmentParents(content.Assets);

        var created = new List<string>();
        try
        {
            foreach (var value in unique)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = AssetBlobPath(value.Asset.Id);
                var existed = File.Exists(path);
                await EnsureAssetBlobAsync(value.Asset, value.Bytes, cancellationToken);
                if (!existed) created.Add(path);
            }
            cancellationToken.ThrowIfCancellationRequested();
            ForgeProjectValidation.Validate(RootPath, Manifest, content);
            Content = content;
        }
        catch
        {
            foreach (var path in created) File.Delete(path);
            throw;
        }
    }

}

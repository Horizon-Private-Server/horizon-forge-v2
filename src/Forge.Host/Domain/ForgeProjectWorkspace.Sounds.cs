namespace Forge.Host.Domain;

public sealed partial class ForgeProjectWorkspace
{
    internal async Task SetSoundsAsync(
        ProjectSoundState state,
        IReadOnlyList<ProjectAssetAttachment>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        attachments ??= [];
        cancellationToken.ThrowIfCancellationRequested();

        var resolved = attachments.Select(value =>
        {
            ArgumentNullException.ThrowIfNull(value);
            return (Asset: CreateAttachedAsset(
                    value.Kind,
                    value.CanonicalFormatVersion,
                    value.CanonicalBytes,
                    value.ParentId,
                    nameof(attachments),
                    nameof(attachments)),
                value.CanonicalBytes);
        }).ToArray();
        var unique = resolved.GroupBy(value => value.Asset.Id).Select(group =>
        {
            var first = group.First();
            if (group.Any(value => value.Asset != first.Asset
                    || !value.CanonicalBytes.Span.SequenceEqual(first.CanonicalBytes.Span)))
                throw new InvalidDataException($"Sound asset {group.Key} has inconsistent transaction data.");
            return first;
        }).ToArray();
        var existing = Content.Assets.ToDictionary(value => value.Id);
        foreach (var value in unique)
            if (existing.TryGetValue(value.Asset.Id, out var current) && current != value.Asset)
                throw new InvalidDataException($"Sound asset {value.Asset.Id} has inconsistent metadata.");

        var added = unique.Where(value => !existing.ContainsKey(value.Asset.Id)).ToArray();
        var content = Content with
        {
            Sounds = state,
            Assets = Content.Assets.Concat(added.Select(value => value.Asset))
                .OrderBy(value => value.Id.ToString(), StringComparer.Ordinal).ToArray(),
        };
        ForgeProjectValidation.Validate(RootPath, Manifest, content);
        foreach (var value in added)
            await EnsureAssetBlobAsync(value.Asset, value.CanonicalBytes, cancellationToken);
        Content = content;
    }
}

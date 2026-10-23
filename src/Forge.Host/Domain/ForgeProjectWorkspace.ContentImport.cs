namespace Forge.Host.Domain;

public sealed partial class ForgeProjectWorkspace
{
    internal void CommitContentImportRemoval(ContentImportRemovalCommit command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Content);
        if (!string.Equals(command.ExpectedTargetFingerprint, CurrentFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The import-removal preview no longer matches the active project.");
        if (!Content.ImportBatches.Any(value => value.BatchId == command.BatchId))
            throw new InvalidOperationException($"Import batch {command.BatchId} no longer exists.");
        if (!SameJson(command.Content.ImportBatches, Content.ImportBatches))
            throw new InvalidOperationException("Import removal cannot rewrite the append-only batch ledger.");
        ForgeProjectValidation.Validate(RootPath, Manifest, command.Content);
        Content = command.Content;
    }

    internal async Task CommitContentImportAsync(
        ContentImportBatchCommit command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Batch);
        ArgumentNullException.ThrowIfNull(command.Content);
        ArgumentNullException.ThrowIfNull(command.Attachments);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(command.ExpectedTargetFingerprint, CurrentFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The import plan target no longer matches the active project.");
        if (Content.ImportBatches.Any(value => value.BatchId == command.Batch.BatchId))
            throw new InvalidOperationException($"Import batch {command.Batch.BatchId} is already committed.");
        if (!SameJson(command.Content.Assets, Content.Assets)
            || !SameJson(command.Content.ImportBatches, Content.ImportBatches))
            throw new InvalidOperationException(
                "Import content must retain existing assets and batch records; the transaction owns those changes.");

        var resolved = command.Attachments.Select(attachment =>
        {
            ArgumentNullException.ThrowIfNull(attachment);
            return (Asset: CreateAttachedAsset(
                    attachment.Kind,
                    attachment.CanonicalFormatVersion,
                    attachment.CanonicalBytes,
                    attachment.ParentId,
                    nameof(command.Attachments),
                    nameof(command.Attachments)),
                Bytes: attachment.CanonicalBytes);
        }).ToArray();
        var unique = resolved.GroupBy(value => value.Asset.Id).Select(group =>
        {
            var first = group.First();
            if (group.Any(value => value.Asset != first.Asset
                    || !value.Bytes.Span.SequenceEqual(first.Bytes.Span)))
                throw new InvalidDataException($"Project asset {group.Key} has inconsistent transaction data.");
            return first;
        }).ToArray();

        var existing = Content.Assets.ToDictionary(asset => asset.Id);
        foreach (var value in unique)
            if (existing.TryGetValue(value.Asset.Id, out var current) && current != value.Asset)
                throw new InvalidDataException($"Project asset {value.Asset.Id} has inconsistent metadata.");
        var added = unique.Where(value => !existing.ContainsKey(value.Asset.Id))
            .Select(value => value.Asset).ToArray();
        ValidateAttachmentParents(Content.Assets.Concat(added));
        var content = command.Content with
        {
            Assets = Content.Assets.Concat(added)
                .OrderBy(asset => asset.Id.ToString(), StringComparer.Ordinal).ToArray(),
            ImportBatches = Content.ImportBatches.Append(command.Batch).ToArray(),
        };

        Checkpoint(command, ContentImportCommitPhase.Prepared, cancellationToken);
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
            Checkpoint(command, ContentImportCommitPhase.BlobsStaged, cancellationToken);
            ForgeProjectValidation.Validate(RootPath, Manifest, content);
            Checkpoint(command, ContentImportCommitPhase.Validated, cancellationToken);
            Checkpoint(command, ContentImportCommitPhase.Publishing, cancellationToken);
            Content = content;
        }
        catch
        {
            foreach (var path in created) File.Delete(path);
            throw;
        }
    }

    private static bool SameJson<T>(T left, T right) =>
        ForgeProjectPersistence.Serialize(left).AsSpan()
            .SequenceEqual(ForgeProjectPersistence.Serialize(right));

    private static void ValidateAttachmentParents(IEnumerable<ProjectAttachedAsset> assets)
    {
        var known = assets.ToDictionary(value => value.Id);
        foreach (var asset in known.Values)
        {
            var visited = new HashSet<AssetId> { asset.Id };
            var parent = asset.ParentId;
            while (parent is { } parentId && known.TryGetValue(parentId, out var parentAsset))
            {
                if (!visited.Add(parentId))
                    throw new InvalidDataException($"Project asset {asset.Id} has a cyclic parent chain.");
                parent = parentAsset.ParentId;
            }
        }
    }

    private static void Checkpoint(
        ContentImportBatchCommit command,
        ContentImportCommitPhase phase,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        command.FaultInjector?.Invoke(phase);
        cancellationToken.ThrowIfCancellationRequested();
    }
}

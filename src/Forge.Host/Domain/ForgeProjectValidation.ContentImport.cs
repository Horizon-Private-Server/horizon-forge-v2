namespace Forge.Host.Domain;

internal static partial class ForgeProjectValidation
{
    private static void ValidateImportBatches(IReadOnlyList<ContentImportBatch> batches)
    {
        if (batches.Count > ContentImportBatchSchema.MaximumBatches
            || batches.Any(batch => batch is null))
            throw new InvalidDataException("Project import batch records are invalid.");
        if (batches.Select(batch => batch.BatchId).Distinct(StringComparer.Ordinal).Count() != batches.Count)
            throw new InvalidDataException("Project contains duplicate import batch IDs.");
        foreach (var batch in batches)
        {
            if (batch.SchemaVersion != ContentImportBatchSchema.CurrentVersion
                || batch.PlanSchemaVersion < 1 || batch.CapabilitySchemaVersion < 1
                || batch.ImportedUnixMilliseconds < 0 || batch.SourceLevel is < 0 or > 999
                || batch.BatchId is not { Length: 39 }
                || !batch.BatchId.StartsWith("import-", StringComparison.Ordinal)
                || batch.BatchId[7..].Any(character => !char.IsAsciiHexDigitLower(character))
                || batch.SourceFingerprint is not { Length: 32 }
                || batch.SourceFingerprint.Any(character => !Uri.IsHexDigit(character))
                || batch.SourceLevelHash is not { Length: 64 }
                || batch.SourceLevelHash.Any(character => !char.IsAsciiHexDigitLower(character)))
                throw new InvalidDataException("Project import batch identity is invalid.");
            ValidateText(batch.SourceGame, nameof(batch.SourceGame));
            ValidateText(batch.SourceRegion, nameof(batch.SourceRegion));
            ValidateText(batch.SourceRevision, nameof(batch.SourceRevision));
            if (batch.Families is null || batch.Families.Count is 0
                || batch.Families.Count > ContentImportBatchSchema.MaximumFamilies
                || batch.Families.Any(family => family is null
                    || !ValidFamilyIdentifier(family.Family)
                    || family.Mode is not (ContentImportMode.Add or ContentImportMode.Replace))
                || batch.Families.Select(family => family.Family)
                    .Distinct(StringComparer.Ordinal).Count() != batch.Families.Count
                || !batch.Families.Select(family => family.Family)
                    .SequenceEqual(batch.Families.Select(family => family.Family).Order(StringComparer.Ordinal)))
                throw new InvalidDataException($"Import batch {batch.BatchId} family modes are invalid.");
            ValidateStableIds(batch.BatchId, "added", batch.Added);
            ValidateStableIds(batch.BatchId, "reused", batch.Reused);
            ValidateStableIds(batch.BatchId, "replaced", batch.Replaced);
            ValidateStableIds(batch.BatchId, "protected", batch.Protected);
        }
    }

    private static void ValidateStableIds(string batchId, string name, IReadOnlyList<string>? values)
    {
        if (values is null || values.Count > ContentImportBatchSchema.MaximumStableIdsPerSet
            || values.Any(value => value is not { Length: >= 1 and <= 256 }
                || Path.IsPathRooted(value) || value.IndexOfAny(['/', '\\']) >= 0
                || value.Any(char.IsControl))
            || values.Distinct(StringComparer.Ordinal).Count() != values.Count
            || !values.SequenceEqual(values.Order(StringComparer.Ordinal)))
            throw new InvalidDataException($"Import batch {batchId} {name} IDs are invalid.");
    }

    private static bool ValidFamilyIdentifier(string? value) => value is { Length: >= 1 and <= 128 }
        && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
            or >= '0' and <= '9' or '.' or '-' or '_');
}

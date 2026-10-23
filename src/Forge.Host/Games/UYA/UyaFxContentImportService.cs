using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal static class UyaFxContentImportService
{
    internal static async Task<ContentImportBatchCommit> PrepareCommitAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        ProjectFxState? source,
        ContentImportPlan plan,
        long? importedUnixMilliseconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(plan);
        var family = UyaContentImportValidation.RequireSingleFamily(
            workspace, plan, "fx", ContentImportMode.Replace,
            "This importer accepts one replace-only FX family.");
        if (workspace.Content.Fx is null
            || !family.Remove.SequenceEqual(["fx:project"], StringComparer.Ordinal))
            throw new InvalidOperationException("FX replacement requires one existing target FX inventory.");
        var donor = source ?? throw new InvalidDataException("The donor FX inventory is missing.");
        if (donor.ReplaceSourceBank || donor.Additions.Count != 0 || donor.SourceTextures.Count == 0
            || donor.SourceTextures.Any(value => value.Texture is null || value.Diagnostic is not null))
            throw new InvalidDataException("The donor FX inventory is not a complete clean source-map bank.");

        var attachments = new ProjectAssetAttachmentCollector();
        var donorTextures = donor.SourceTextures.Select(value => value.Texture!).Distinct().ToArray();
        foreach (var texture in donorTextures)
            await AttachTextureAsync(
                workspace, catalog, attachments, texture, plan.SourceLevel, plan.SourceFingerprint,
                cancellationToken);

        var previousSources = workspace.Content.Fx.SourceTextures.Select(value => value.Texture)
            .OfType<ProjectAssetReference>().ToHashSet();
        var content = workspace.Content with
        {
            Fx = donor with { ReplaceSourceBank = true },
            AssetOverrides = workspace.Content.AssetOverrides
                .Where(value => !previousSources.Contains(value.Source)).ToArray(),
        };
        var attachmentIds = donorTextures.Select(value => value.Id).ToArray();
        var existing = workspace.Content.Assets.Select(value => value.Id).ToHashSet();
        var batch = new ContentImportBatch(
            ContentImportBatchSchema.CurrentVersion,
            plan.BatchId,
            plan.SchemaVersion,
            plan.CapabilitySchemaVersion,
            importedUnixMilliseconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            plan.Game,
            plan.Region,
            plan.Revision,
            plan.SourceFingerprint,
            plan.SourceLevelHash,
            plan.SourceLevel,
            [new("fx", ContentImportMode.Replace)],
            attachmentIds.Where(value => !existing.Contains(value)).Select(value => $"asset:{value}")
                .Order(StringComparer.Ordinal).ToArray(),
            attachmentIds.Where(existing.Contains).Select(value => $"asset:{value}")
                .Order(StringComparer.Ordinal).ToArray(),
            ["fx:project"],
            []);
        return new(plan.TargetFingerprint, batch, content, attachments.Attachments);
    }

    private static async Task AttachTextureAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        ProjectAssetAttachmentCollector attachments,
        ProjectAssetReference reference,
        int sourceLevel,
        string sourceFingerprint,
        CancellationToken cancellationToken)
    {
        if (reference.Kind != AssetKind.Texture)
            throw new InvalidDataException($"Donor FX texture {reference.Id} has an invalid kind.");
        var attached = workspace.Content.Assets.SingleOrDefault(value => value.Id == reference.Id);
        if (attached is not null)
        {
            if (attached.Kind != AssetKind.Texture
                || attached.CanonicalFormatVersion != ProjectTextureAssetSchema.CanonicalFormatVersion)
                throw new InvalidDataException($"Project asset {reference.Id} conflicts with the donor FX texture.");
            return;
        }

        var entry = catalog.Query(new(Id: reference.Id)).SingleOrDefault()
            ?? throw new FileNotFoundException($"Donor FX texture {reference.Id} is missing from the catalog.");
        var level = $"level{sourceLevel:00}";
        if (entry.Kind != AssetKind.Texture
            || entry.CanonicalFormatVersion != ProjectTextureAssetSchema.CanonicalFormatVersion
            || !entry.Sources.Any(value => value.Game == "UYA" && value.Region == "NTSC-U"
                && value.Revision == "1.00" && value.Level == level
                && value.Fingerprint.Equals(sourceFingerprint, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Donor FX texture {reference.Id} has no matching {level} source identity.");
        var path = catalog.ResolveBlobPath(reference.Id)
            ?? throw new FileNotFoundException($"Donor FX texture blob {reference.Id} is missing.");
        var bytes = await AssetCatalogBlobReader.ReadVerifiedAsync(
            entry, path, ProjectTextureAssetSchema.MaximumCanonicalBytes, cancellationToken);
        var attachedReference = attachments.Attach(
            AssetKind.Texture, ProjectTextureAssetSchema.CanonicalFormatVersion, bytes);
        if (attachedReference != reference)
            throw new InvalidDataException($"Donor FX texture {reference.Id} has inconsistent content identity.");
    }
}

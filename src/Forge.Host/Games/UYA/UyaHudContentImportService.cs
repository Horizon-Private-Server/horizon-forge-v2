using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal sealed record UyaHudContentImportSource(
    ProjectHudState? Hud,
    IReadOnlyList<OpaqueSectionCapture> OpaqueSections);

internal static class UyaHudContentImportService
{
    internal static async Task<ContentImportBatchCommit> PrepareCommitAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        UyaHudContentImportSource source,
        ContentImportPlan plan,
        long? importedUnixMilliseconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        var family = UyaContentImportValidation.RequireSingleFamily(
            workspace, plan, "hud", ContentImportMode.Replace,
            "This importer accepts one replace-only HUD family.");
        if (workspace.Content.Hud is null
            || !family.Remove.SequenceEqual(["hud:project"], StringComparer.Ordinal))
            throw new InvalidOperationException("HUD replacement requires one existing target HUD inventory.");
        var donor = source.Hud ?? throw new InvalidDataException("The donor HUD inventory is missing.");
        if (donor.SourcePayload is not null || donor.Additions.Count != 0)
            throw new InvalidDataException("The donor HUD inventory is not a clean source-map inventory.");

        var attachments = new ProjectAssetAttachmentCollector();
        var payload = attachments.Attach(
            AssetKind.World,
            UyaHudImportPayload.CanonicalFormatVersion,
            UyaHudImportPayload.Encode(source.OpaqueSections));
        var donorTextures = donor.SourceIcons.SelectMany(value => value.Frames)
            .Select(value => value.Texture).OfType<ProjectAssetReference>().Distinct().ToArray();
        foreach (var texture in donorTextures)
            await AttachTextureAsync(
                workspace, catalog, attachments, texture, plan.SourceLevel, plan.SourceFingerprint,
                cancellationToken);

        var previousSources = workspace.Content.Hud.SourceIcons.SelectMany(value => value.Frames)
            .Select(value => value.Texture).OfType<ProjectAssetReference>().ToHashSet();
        var content = workspace.Content with
        {
            Hud = donor with { SourcePayload = payload },
            AssetOverrides = workspace.Content.AssetOverrides
                .Where(value => !previousSources.Contains(value.Source)).ToArray(),
        };
        var existing = workspace.Content.Assets.Select(value => value.Id).ToHashSet();
        var attachmentIds = donorTextures.Select(value => value.Id).Append(payload.Id).Distinct().ToArray();
        var newAttachments = attachments.Attachments.Where(attachment => !existing.Contains(AssetId.Compute(
            attachment.Kind, attachment.CanonicalFormatVersion, attachment.CanonicalBytes.Span))).ToArray();
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
            [new("hud", ContentImportMode.Replace)],
            attachmentIds.Where(value => !existing.Contains(value)).Select(value => $"asset:{value}")
                .Order(StringComparer.Ordinal).ToArray(),
            attachmentIds.Where(existing.Contains).Select(value => $"asset:{value}")
                .Order(StringComparer.Ordinal).ToArray(),
            ["hud:project"],
            []);
        return new(plan.TargetFingerprint, batch, content, newAttachments);
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
            throw new InvalidDataException($"Donor HUD texture {reference.Id} has an invalid kind.");
        var attached = workspace.Content.Assets.SingleOrDefault(value => value.Id == reference.Id);
        if (attached is not null)
        {
            if (attached.Kind != AssetKind.Texture
                || attached.CanonicalFormatVersion != ProjectTextureAssetSchema.CanonicalFormatVersion)
                throw new InvalidDataException($"Project asset {reference.Id} conflicts with the donor HUD texture.");
            return;
        }

        var entry = catalog.Query(new(Id: reference.Id)).SingleOrDefault()
            ?? throw new FileNotFoundException($"Donor HUD texture {reference.Id} is missing from the catalog.");
        var level = $"level{sourceLevel:00}";
        if (entry.Kind != AssetKind.Texture
            || entry.CanonicalFormatVersion != ProjectTextureAssetSchema.CanonicalFormatVersion
            || !entry.Sources.Any(value => value.Game == "UYA" && value.Region == "NTSC-U"
                && value.Revision == "1.00" && value.Level == level
                && value.Fingerprint.Equals(sourceFingerprint, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Donor HUD texture {reference.Id} has no matching {level} source identity.");
        var path = catalog.ResolveBlobPath(reference.Id)
            ?? throw new FileNotFoundException($"Donor HUD texture blob {reference.Id} is missing.");
        var bytes = await AssetCatalogBlobReader.ReadVerifiedAsync(
            entry, path, ProjectTextureAssetSchema.MaximumCanonicalBytes, cancellationToken);
        var attachedReference = attachments.Attach(
            AssetKind.Texture, ProjectTextureAssetSchema.CanonicalFormatVersion, bytes);
        if (attachedReference != reference)
            throw new InvalidDataException($"Donor HUD texture {reference.Id} has inconsistent content identity.");
    }
}

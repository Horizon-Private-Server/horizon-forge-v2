using Forge.Host.Domain;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Games.UYA.Level;

namespace Forge.Host.Games.UYA;

internal static class UyaOcclusionContentImportService
{
    internal static ContentImportBatchCommit PrepareCommit(
        ForgeProjectWorkspace workspace,
        UyaOcclusionImportSource? source,
        ContentImportPlan plan,
        long? importedUnixMilliseconds = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(plan);
        var family = UyaContentImportValidation.RequireSingleFamily(
            workspace, plan, "occlusion", ContentImportMode.Replace,
            "This importer accepts one replace-only occlusion family.");
        if (!family.Remove.SequenceEqual(["occlusion:project"], StringComparer.Ordinal))
            throw new InvalidOperationException("This importer accepts one replace-only occlusion family.");
        var donor = source ?? throw new InvalidDataException("The donor occlusion payload is missing.");
        _ = UyaOcclusionMappingsReader.Read(donor.GameplayMappings);
        _ = UyaOcclusionGridReader.Read(donor.AssetGrid);

        var attachments = new ProjectAssetAttachmentCollector();
        var payload = attachments.Attach(
            AssetKind.World,
            ProjectOcclusionSchema.SourcePayloadCanonicalFormatVersion,
            UyaOcclusionImportPayload.Encode(donor));
        var existing = workspace.Content.Assets.Select(value => value.Id).ToHashSet();
        var content = workspace.Content with
        {
            Occlusion = new(ProjectOcclusionSchema.CurrentVersion, plan.SourceLevel, payload),
        };
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
            [new("occlusion", ContentImportMode.Replace)],
            existing.Contains(payload.Id) ? [] : [$"asset:{payload.Id}"],
            existing.Contains(payload.Id) ? [$"asset:{payload.Id}"] : [],
            ["occlusion:project"],
            []);
        var newAttachments = attachments.Attachments.Where(value => !existing.Contains(AssetId.Compute(
            value.Kind, value.CanonicalFormatVersion, value.CanonicalBytes.Span))).ToArray();
        return new(plan.TargetFingerprint, batch, content, newAttachments);
    }
}

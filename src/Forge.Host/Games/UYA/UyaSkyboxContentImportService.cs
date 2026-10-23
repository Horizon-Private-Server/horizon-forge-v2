using System.Security.Cryptography;
using System.Text;
using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Skyboxes;

namespace Forge.Host.Games.UYA;

internal sealed record UyaSkyboxContentImportSource(
    IReadOnlyList<ProjectEntity> Entities,
    IReadOnlyList<UyaBaseLayerPayload> BaseLayers);

internal static class UyaSkyboxContentImportService
{
    internal static async Task<ContentImportBatchCommit> PrepareCommitAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        UyaSkyboxContentImportSource source,
        ContentImportPlan plan,
        long? importedUnixMilliseconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        var family = UyaContentImportValidation.RequireSingleFamily(
            workspace, plan, "skybox", ContentImportMode.Replace,
            "This importer accepts one replace-only skybox family.");
        if (family.Protect.Count > 0)
            throw new InvalidOperationException("The target skybox contains protected shells and cannot be replaced.");

        var targetShells = workspace.Content.Entities.Where(value => value.SkyShell is not null).ToArray();
        var removed = family.Remove.Select(value =>
        {
            if (!value.StartsWith("entity:", StringComparison.Ordinal))
                throw new InvalidDataException("Skybox replacement contains an invalid target ID.");
            var id = EntityId.Parse(value["entity:".Length..]);
            if (!targetShells.Any(shell => shell.EntityId == id))
                throw new InvalidOperationException("The import plan references a missing target sky shell.");
            return id;
        }).ToHashSet();
        if (removed.Count != targetShells.Length)
            throw new InvalidOperationException("Skybox replacement must remove the complete target composition.");

        var payload = source.BaseLayers.SingleOrDefault(value =>
                value is { Layer: BakeLayerId.Sky, Name: "sky.bin", Kind: AssetKind.Sky })
            ?? throw new InvalidDataException("The donor skybox payload is missing.");
        var assetId = AssetId.Compute(
            AssetKind.Sky, UyaBaseLayerSchema.CanonicalFormatVersion, payload.Bytes);
        using var stream = new MemoryStream(payload.Bytes, writable: false);
        var decoded = SkyboxReader.Read(stream, GameId.UYA);
        var donorShells = source.Entities.Where(value => value.SkyShell is not null)
            .OrderBy(value => value.SkyShell!.Order).ToArray();
        if (donorShells.Length != decoded.Shells.Count
            || !donorShells.Select(value => value.SkyShell!.Order)
                .SequenceEqual(Enumerable.Range(0, donorShells.Length))
            || donorShells.Any(value => value.Asset is not { Kind: AssetKind.Sky } asset
                || asset.Id != assetId
                || value.Provenance is not { Game: "UYA", Section: "level_wad/assets/sky" }
                || value.SkyShell!.SourceShellIndex < 0
                || value.SkyShell.SourceShellIndex >= decoded.Shells.Count))
            throw new InvalidDataException("The donor skybox inventory is incomplete or invalid.");

        var imported = donorShells.Select(value => value with
        {
            EntityId = new EntityId(StableGuid(
                $"{plan.BatchId}\nentity\nskybox\n{value.SkyShell!.SourceShellIndex}\n{assetId}")),
            Name = $"Imported {value.Name}",
            State = null,
            Provenance = value.Provenance! with { Level = plan.SourceLevel },
        }).ToArray();
        var entities = workspace.Content.Entities.Where(value => !removed.Contains(value.EntityId))
            .Concat(imported).ToArray();
        var content = workspace.Content with { Entities = entities };
        await UyaSkyShellEditorService.ComposeAsync(
            workspace.CreateProjection(content), catalog, entities, cancellationToken);

        var attachment = workspace.Content.Assets.Any(value => value.Id == assetId)
            ? null
            : new ProjectAssetAttachment(
                AssetKind.Sky, UyaBaseLayerSchema.CanonicalFormatVersion, payload.Bytes);
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
            [new("skybox", ContentImportMode.Replace)],
            imported.Select(value => $"entity:{value.EntityId}")
                .Concat(attachment is null ? [] : [$"asset:{assetId}"])
                .Order(StringComparer.Ordinal).ToArray(),
            attachment is null ? [$"asset:{assetId}"] : [],
            removed.Select(value => $"entity:{value}").Order(StringComparer.Ordinal).ToArray(),
            []);
        return new(plan.TargetFingerprint, batch, content, attachment is null ? [] : [attachment]);
    }

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> guid = stackalloc byte[16];
        bytes.AsSpan(0, guid.Length).CopyTo(guid);
        guid[7] = (byte)((guid[7] & 0x0f) | 0x40);
        guid[8] = (byte)((guid[8] & 0x3f) | 0x80);
        return new(guid, bigEndian: true);
    }
}

using System.Security.Cryptography;
using System.Text;
using Forge.Host.Domain;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.Games.UYA;

internal static class UyaDirectionalLightsContentImportService
{
    internal const int MaximumLightCount = UyaDirectionalLightsWriter.MaximumSelectableLights;

    internal static async Task<ContentImportBatchCommit> PrepareCommitAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        IReadOnlyList<ProjectEntity> donorEntities,
        ContentImportPlan plan,
        long? importedUnixMilliseconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(donorEntities);
        ArgumentNullException.ThrowIfNull(plan);
        var family = UyaContentImportValidation.RequireSingleFamily(
            workspace, plan, "directionalLights", ContentImportMode.Replace,
            "This importer accepts one replace-only directional-light family.");
        if (family.Protect.Count > 0)
            throw new InvalidOperationException("The target directional-light array contains protected lights.");

        var target = workspace.Content.Entities.Where(IsDirectionalLight).ToArray();
        var removed = family.Remove.Select(value =>
        {
            if (!value.StartsWith("entity:", StringComparison.Ordinal))
                throw new InvalidDataException("Directional-light replacement contains an invalid target ID.");
            var id = EntityId.Parse(value["entity:".Length..]);
            if (!target.Any(light => light.EntityId == id))
                throw new InvalidOperationException("The import plan references a missing target directional light.");
            return id;
        }).ToHashSet();
        if (removed.Count != target.Length)
            throw new InvalidOperationException("Directional-light replacement must remove the complete target array.");

        var donor = donorEntities.Where(IsDirectionalLight)
            .OrderBy(value => value.Provenance!.SourceIndex).ToArray();
        if (donor.Length is 0 or > MaximumLightCount || donor.Length != family.Add.Count
            || donor.Select(value => value.Provenance?.SourceIndex)
                .SequenceEqual(Enumerable.Range(0, donor.Length).Select(value => (int?)value)) == false
            || donor.Any(value => value.Provenance is not
                { Game: "UYA", Section: "gameplay/core/directional_lights" }))
            throw new InvalidDataException("The donor directional-light inventory is incomplete or invalid.");
        var invalidTie = workspace.Content.Entities.FirstOrDefault(value => value.TieLighting is { } lighting
            && !SelectorFits(lighting.DirectionalLights, donor.Length));
        if (invalidTie is not null)
            throw new InvalidOperationException(
                $"TIE {invalidTie.Name} ({invalidTie.EntityId}) has a directional-light selector that does not fit the donor array.");

        var imported = donor.Select(value => value with
        {
            EntityId = new EntityId(StableGuid(
                $"{plan.BatchId}\nentity\ndirectionalLights\n{value.Provenance!.SourceIndex}")),
            Name = $"Imported {value.Name}",
            State = null,
            Provenance = value.Provenance! with { Level = plan.SourceLevel },
        }).ToArray();
        var content = workspace.Content with
        {
            Entities = workspace.Content.Entities.Where(value => !removed.Contains(value.EntityId))
                .Concat(imported).ToArray(),
        };
        await UyaBaseLayerStore.ValidateDirectionalLightsAsync(
            workspace.CreateProjection(content), catalog, cancellationToken);

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
            [new("directionalLights", ContentImportMode.Replace)],
            imported.Select(value => $"entity:{value.EntityId}").Order(StringComparer.Ordinal).ToArray(),
            [],
            removed.Select(value => $"entity:{value}").Order(StringComparer.Ordinal).ToArray(),
            []);
        return new(plan.TargetFingerprint, batch, content, []);
    }

    internal static bool SelectorFits(int selector, int lightCount)
    {
        if (lightCount is <= 0 or > MaximumLightCount) return false;
        var packed = unchecked((uint)selector);
        if (packed == 0x0f) return true;
        if ((packed & 0x0f) >= lightCount) return false;
        var blend = (packed >> 8) & 0xff;
        return blend == 0 || ((packed >> 4) & 0x0f) < lightCount;
    }

    private static bool IsDirectionalLight(ProjectEntity value) =>
        value.Lighting?.DirectionalLight is not null;

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

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Sounds;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

public sealed record UyaMobyContentImportRequest(
    string ProjectPath,
    string CatalogRootPath,
    string SourceIsoPath,
    string SourceFingerprint,
    int SourceLevel,
    string BatchId,
    ContentImportMode Mode,
    IReadOnlyList<UyaMobyExternalReferenceChoice>? ReferenceChoices = null);

public enum UyaMobyExternalReferenceAction : byte
{
    Map = 1,
    Clear = 2,
}

public sealed record UyaMobyExternalReferenceChoice(
    int OwnerSourceIndex,
    string FieldKey,
    int SourceValue,
    UyaMobyExternalReferenceAction Action,
    EntityId? TargetEntityId = null);

public sealed record UyaMobyExternalReferenceRequirement(
    int OwnerSourceIndex,
    string FieldKey,
    int SourceValue,
    ProjectEntityKind TargetKind,
    bool Nullable);

internal sealed record UyaMobyContentImportSource(
    IReadOnlyList<ProjectEntity> Entities,
    ProjectMobyPVarState? MobyPVars,
    ProjectSoundState Sounds,
    IReadOnlyList<OpaqueSectionCapture> OpaqueSections);

internal static partial class UyaMobyContentImportService
{
    public static async Task<UyaStaticContentImportResult> ImportAsync(
        EditorRuntime runtime,
        UyaMobyContentImportRequest request,
        Func<IsoProgress, ValueTask>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Mode is not (ContentImportMode.Add or ContentImportMode.Replace))
            throw new ArgumentOutOfRangeException(nameof(request), "Moby import requires add or replace mode.");
        var context = await UyaContentImportPlanningService.PrepareAsync(new(
            request.ProjectPath,
            request.CatalogRootPath,
            request.SourceIsoPath,
            request.SourceFingerprint,
            request.SourceLevel,
            [new("mobys", request.Mode)]), progress, cancellationToken);
        if (context.Plan.BatchId != request.BatchId)
            throw new InvalidOperationException("The import plan changed; review the refreshed plan before importing.");
        var command = await PrepareCommitAsync(
            context.Workspace,
            context.Catalog,
            new(context.Source.Entities, context.Source.MobyPVars, context.Source.Sounds,
                context.Source.OpaqueSections),
            context.Plan,
            referenceChoices: request.ReferenceChoices,
            cancellationToken: cancellationToken);
        await runtime.CommitContentImportAsync(command, cancellationToken);
        var family = context.Plan.Families.Single(value => value.Family == "mobys");
        return new(
            command.Batch.BatchId,
            command.Batch.Added.Count(value => value.StartsWith("entity:", StringComparison.Ordinal)),
            command.Batch.Replaced.Count,
            family.Protect.Count,
            command.Attachments.Count);
    }

    internal static async Task<ContentImportBatchCommit> PrepareCommitAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        UyaMobyContentImportSource source,
        ContentImportPlan plan,
        long? importedUnixMilliseconds = null,
        MobyDexCatalog? mobyDex = null,
        IReadOnlyList<UyaMobyExternalReferenceChoice>? referenceChoices = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        var family = UyaContentImportValidation.RequireSingleFamily(
            workspace, plan, "mobys", ContentImportMode.AddAndReplace,
            "This importer accepts one selected Moby family only.");

        var removed = RemovalSet(workspace, family);
        var retained = workspace.Content.Entities.Where(value => !removed.Contains(value.EntityId)).ToArray();
        var donor = DonorMobys(source.Entities);
        var donorById = donor.ToDictionary(value => value.EntityId);
        var targetIds = donor.ToDictionary(
            value => value.EntityId,
            value => new EntityId(StableGuid(
                $"{plan.BatchId}\nentity\nmobys\n{value.Provenance!.SourceIndex}\n{value.Asset?.Id}")));
        var groupImport = ImportGroups(source, plan, donor, retained, targetIds);

        var donorClasses = donor.Where(value => value.Asset is { Kind: AssetKind.Moby })
            .Select(value => new UyaDonorStaticClass(value.Source!.ClassId, value.Asset!.Id)).Distinct().ToArray();
        var retainedClasses = retained.Where(value => value.Layer == "mobys" && value.Asset is { Kind: AssetKind.Moby })
            .Select(value => new UyaDonorStaticClass(value.Source!.ClassId, value.Asset!.Id)).Distinct().ToArray();
        var rebase = UyaCustomTieClassRegistry.RebaseMobyDonor(
            workspace.Content.CustomClasses, donorClasses, retainedClasses);
        var remaps = rebase.Remaps.ToDictionary(
            value => (value.SourceClassId, value.AssetId), value => value.TargetClassId);
        ValidateModelLessClasses(donor, retained, remaps.Values);

        var usedUids = retained.Where(value => value.Layer == "mobys")
            .Select(value => UyaMobyInstancesReader.ReadInstance(value.Source!.RawRecord).Uid)
            .Where(value => value >= 0).ToHashSet();
        var imported = new List<ProjectEntity>(donor.Length);
        var attachments = new Dictionary<AssetId, ProjectAssetAttachment>();
        var existingAssets = workspace.Content.Assets.Select(value => value.Id).ToHashSet();
        foreach (var entity in donor)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceRecord = entity.Source!;
            var asset = entity.Asset;
            var targetClass = asset is not null
                ? remaps[(sourceRecord.ClassId, asset.Id)]
                : sourceRecord.ClassId;
            var raw = sourceRecord.RawRecord.ToArray();
            var instance = UyaMobyInstancesReader.ReadInstance(raw);
            var uid = instance.Uid >= 0 && usedUids.Add(instance.Uid)
                ? instance.Uid
                : NextUid(usedUids);
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x10), uid);
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x28), targetClass);
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x58),
                instance.Group < 0 ? -1 : groupImport.TargetIds[instance.Group]);
            imported.Add(entity with
            {
                EntityId = targetIds[entity.EntityId],
                Name = $"Imported {entity.Name}",
                State = null,
                Source = sourceRecord with
                {
                    ClassId = targetClass,
                    RawRecord = raw,
                    SourceIndex = entity.Provenance!.SourceIndex,
                },
            });
            if (asset is not null
                && !existingAssets.Contains(asset.Id)
                && !attachments.ContainsKey(asset.Id))
                attachments.Add(asset.Id, await ReadMobyAttachmentAsync(
                    catalog, asset.Id, sourceRecord.ClassId, plan, cancellationToken));
        }

        var entities = retained.Concat(imported).ToArray();
        var pvars = ImportPVars(workspace.Content.MobyPVars, source.MobyPVars,
            donorById, targetIds, retained, removed, referenceChoices ?? []);
        var dex = ImportMobyDex(workspace.Content.MobyDex, rebase.Remaps,
            mobyDex ?? new MobyDexCatalog(UyaMobyDexDataset.Create()));
        var existingSoundPrograms = workspace.Content.Sounds.Programs.Where(value => value.Asset is not null)
            .Select(value => value.Asset!.Id).ToHashSet();
        var sounds = await ImportSoundsAsync(
            workspace,
            catalog,
            source,
            plan,
            imported,
            donor,
            rebase.Remaps,
            retained,
            attachments,
            cancellationToken);
        var usedAssets = entities.Select(value => value.Asset).OfType<ProjectAssetReference>().ToHashSet();
        var registry = rebase.Registry with
        {
            Allocations = rebase.Registry.Allocations.Where(value =>
                    value.Provenance.Origin != ProjectCustomClassOrigin.DonorMap || usedAssets.Contains(value.Asset))
                .OrderBy(value => value.Asset.Kind).ThenBy(value => value.ClassId)
                .ThenBy(value => value.Asset.Id.ToString(), StringComparer.Ordinal).ToArray(),
        };
        var importedGroupIds = ImportedGroupIds(workspace.Content);
        var content = workspace.Content with
        {
            Entities = entities,
            Groups = CleanGroups(workspace.Content.Groups, removed)
                .Where(value => value.Members.Count > 0 || !importedGroupIds.Contains(value.GroupId))
                .Concat(groupImport.Groups).ToArray(),
            MobyPVars = pvars,
            MobyDex = dex,
            Sounds = sounds,
            CustomClasses = registry,
        };
        var added = imported.Select(value => $"entity:{value.EntityId}")
            .Concat(groupImport.Groups.Select(value => $"group:{value.GroupId}"))
            .Concat(attachments.Keys.Select(value => $"asset:{value}"))
            .Concat(registry.Allocations.Except(workspace.Content.CustomClasses.Allocations)
                .Select(value => $"class:moby:{value.ClassId}"))
            .Concat((dex?.Entries ?? []).Except(workspace.Content.MobyDex?.Entries ?? [])
                .Select(value => $"mobydex:{value.Game}:{value.OClass}"))
            .Concat(sounds.Programs.Where(value => value.Asset is not null
                    && !existingSoundPrograms.Contains(value.Asset.Id))
                .Select(value => $"sound:{value.Asset!.Id}"))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var reused = family.Reuse.Concat(donor.Select(value => value.Asset?.Id).OfType<AssetId>()
                .Where(existingAssets.Contains)
                .Select(id => $"asset:{id}"))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
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
            [new("mobys", family.Mode)],
            added,
            reused,
            removed.Select(value => $"entity:{value}").Order(StringComparer.Ordinal).ToArray(),
            family.Protect.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        return new(plan.TargetFingerprint, batch, content, attachments.Values
            .OrderBy(value => AssetId.Compute(value.Kind, value.CanonicalFormatVersion,
                value.CanonicalBytes.Span).ToString(), StringComparer.Ordinal).ToArray());
    }

    private static async Task<ProjectAssetAttachment> ReadMobyAttachmentAsync(
        AssetCatalogStore catalog,
        AssetId id,
        int classId,
        ContentImportPlan plan,
        CancellationToken cancellationToken)
    {
        var entry = catalog.Query(new(Id: id)).SingleOrDefault()
            ?? throw new FileNotFoundException($"Donor Moby asset {id} is missing from the catalog.");
        if (entry.Kind != AssetKind.Moby
            || entry.CanonicalFormatVersion != UyaStaticLayerSchema.CanonicalFormatVersion
            || !AssetCatalogPaging.ClassIds(entry).Contains(classId)
            || !entry.Sources.Any(value => value.Game == "UYA" && value.Region == "NTSC-U"
                && value.Revision == "1.00" && value.Level == $"level{plan.SourceLevel:00}"
                && value.Fingerprint.Equals(plan.SourceFingerprint, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Donor Moby asset {id} has no matching source identity.");
        var path = catalog.ResolveBlobPath(id)
            ?? throw new FileNotFoundException($"Donor Moby asset blob {id} is missing.");
        var bytes = await AssetCatalogBlobReader.ReadVerifiedAsync(
            entry, path, ForgeProjectWorkspace.MaxAttachedAssetBytes, cancellationToken);
        if (UyaCanonicalAssetCodec.Decode(bytes).DefinitionBytes.Length != 0x20)
            throw new InvalidDataException($"Donor Moby asset {id} has an incompatible definition.");
        return new(AssetKind.Moby, entry.CanonicalFormatVersion, bytes);
    }

    private static ProjectEntity[] DonorMobys(IReadOnlyList<ProjectEntity> entities)
    {
        var result = entities.Where(value => value.Layer == "mobys")
            .OrderBy(value => value.Provenance?.SourceIndex).ToArray();
        if (result.Any(value => value.Source?.RawRecord.Length != UyaMobyInstancesReader.RecordSize
                || value.Provenance is not { Game: "UYA", Section: "gameplay/core/moby_instances" }
                || value.Asset is not null and { Kind: not AssetKind.Moby }
                || value.Asset is null && value.Source?.ModelLess != true)
            || result.Select(value => value.Provenance!.SourceIndex).Distinct().Count() != result.Length)
            throw new InvalidDataException("Donor Mobys are missing a unique instance, class, or model identity.");
        return result;
    }

    private static HashSet<EntityId> RemovalSet(
        ForgeProjectWorkspace workspace,
        ContentImportFamilyPlan family)
    {
        var result = new HashSet<EntityId>();
        if (family.Mode != ContentImportMode.Replace) return result;
        var mobys = workspace.Content.Entities.Where(value => value.Layer == "mobys")
            .Select(value => value.EntityId).ToHashSet();
        foreach (var value in family.Remove)
        {
            if (!value.StartsWith("entity:", StringComparison.Ordinal))
                throw new InvalidDataException("Moby replacement contains an invalid target ID.");
            var id = EntityId.Parse(value["entity:".Length..]);
            if (!mobys.Contains(id))
                throw new InvalidOperationException("The Moby import plan references a missing target entity.");
            result.Add(id);
        }
        return result;
    }

    private static void ValidateModelLessClasses(
        IReadOnlyList<ProjectEntity> donor,
        IReadOnlyList<ProjectEntity> retained,
        IEnumerable<int> importedModelClasses)
    {
        var occupied = retained.Where(value => value.Layer == "mobys")
            .GroupBy(value => value.Source!.ClassId).ToDictionary(value => value.Key, value => value.ToArray());
        var modeled = importedModelClasses.ToHashSet();
        foreach (var value in donor.Where(value => value.Asset is null))
            if (modeled.Contains(value.Source!.ClassId)
                || occupied.TryGetValue(value.Source.ClassId, out var target)
                && target.Any(entity => entity.Asset is not null))
                throw new InvalidDataException(
                    $"Model-less donor Moby class 0x{value.Source.ClassId:X4} conflicts with a modeled target class.");
    }

    private static ProjectGroup[] CleanGroups(
        IEnumerable<ProjectGroup> groups,
        IReadOnlySet<EntityId> removed) => groups.Select(group => group with
    {
        Members = group.Members.Where(member => !removed.Contains(member)).ToArray(),
        Template = group.Template is not { } template
            || template.SeedEntityId is { } seed && removed.Contains(seed)
                ? null
                : template with
                {
                    GeneratedMembers = template.GeneratedMembers
                        .Where(member => !removed.Contains(member)).ToArray(),
                },
    }).ToArray();

    private static HashSet<GroupId> ImportedGroupIds(ForgeProjectContent content) => content.ImportBatches
        .SelectMany(value => value.Added)
        .Where(value => value.StartsWith("group:", StringComparison.Ordinal))
        .Select(value => GroupId.Parse(value["group:".Length..]))
        .ToHashSet();

    private static int NextUid(ISet<int> used)
    {
        for (var value = 1; value < int.MaxValue; value++)
            if (used.Add(value)) return value;
        throw new InvalidDataException("The UYA Moby UID space is exhausted.");
    }

    private sealed record MobyGroupImport(
        IReadOnlyDictionary<int, int> TargetIds,
        IReadOnlyList<ProjectGroup> Groups);

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new(bytes);
    }
}

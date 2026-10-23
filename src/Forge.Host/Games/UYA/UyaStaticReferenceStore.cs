using Forge.Host.Domain;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.Games.UYA;

internal static class UyaStaticReferenceStore
{
    public static async Task<UyaStaticReferenceOutputs?> PrepareAsync(
        string projectRoot,
        ForgeProjectWorkspace workspace,
        BakeLayerId layer,
        CancellationToken cancellationToken)
    {
        var projectGroups = PrepareProjectGroups(workspace, layer);
        if (layer == BakeLayerId.Mobys && projectGroups is not null)
            return new(projectGroups, null, null);
        var opaque = await OpaqueContentStore.InspectAsync(projectRoot, cancellationToken);
        if (opaque.Manifest is null) return null;
        if (!opaque.IsValid) throw new InvalidDataException(string.Join(' ', opaque.Blockers));
        var opaqueRoot = ForgeProjectPersistence.ResolveRelativePath(projectRoot, OpaqueContentStore.RelativeRootPath);
        if (layer == BakeLayerId.Mobys)
            return await PrepareMobyGroupsAsync(opaque.Manifest, opaqueRoot, workspace, cancellationToken);
        if (layer == BakeLayerId.Shrubs)
        {
            if (projectGroups is not null) return new(projectGroups, null, null);
            var shrubGroups = opaque.Manifest.Sections.SingleOrDefault(value => value.Name == "gameplay/shrub_groups");
            if (shrubGroups is null) return null;
            var source = SourceEntities(workspace, "gameplay/core/shrub_instances");
            var indices = UyaStaticLayerStore.OrderedEntities(workspace, layer)
                .Select(value => ResolveSourceIndex(
                    value, source, workspace.Manifest.BaseLevel.Level)).ToArray();
            if (indices.SequenceEqual(Enumerable.Range(0, source.Length))) return null;
            return new(UyaTieGroupsWriter.Remap(
                await File.ReadAllBytesAsync(
                    ForgeProjectPersistence.ResolveRelativePath(opaqueRoot, shrubGroups.Blob), cancellationToken),
                indices), null, null);
        }

        var importedOcclusion = await UyaOcclusionImportPayload.ReadAsync(workspace, cancellationToken);
        var occlusion = opaque.Manifest.Sections.SingleOrDefault(value => value.Name == "gameplay/occlusion");
        if (importedOcclusion is null && occlusion is null) return null;
        var occlusionSource = importedOcclusion?.GameplayMappings ?? await File.ReadAllBytesAsync(
            ForgeProjectPersistence.ResolveRelativePath(opaqueRoot, occlusion!.Blob), cancellationToken);
        var sourceLevel = workspace.Content.Occlusion?.SourceLevel ?? workspace.Manifest.BaseLevel.Level;
        var mappings = UyaOcclusionMappingsReader.Read(occlusionSource);
        var ties = UyaStaticLayerStore.OrderedEntities(workspace, BakeLayerId.Ties);
        var sourceTies = SourceEntities(workspace, "gameplay/core/tie_instances");
        var sourceIndices = ties.Select(value => ResolveSourceIndex(value, sourceTies, sourceLevel)).ToArray();
        if (sourceIndices.Any(value => value >= mappings.Ties.Count))
            throw new InvalidDataException("UYA tie source index has no corresponding occlusion mapping.");
        // shortcut: inserted TIEs share one globally visible bit until spatial occlusion can be rebuilt.
        var insertedBitIndex = sourceIndices.Any(value => value < 0)
            ? Enumerable.Range(0, 1024).FirstOrDefault(index => !mappings.Tfrags.Concat(mappings.Ties)
                .Concat(mappings.Mobys).Any(value => value.BitIndex == index), -1)
            : -1;
        if (sourceIndices.Any(value => value < 0) && insertedBitIndex < 0)
            throw new InvalidDataException("UYA occlusion mapping has no free visibility bit for an inserted tie.");
        var occlusionIds = ties.Select(value =>
            UyaTieInstancesReader.ReadInstance(value.Source!.RawRecord).OcclusionId).ToArray();
        if (projectGroups is null
            && importedOcclusion is null
            && sourceIndices.SequenceEqual(Enumerable.Range(0, mappings.Ties.Count))) return null;
        var groups = opaque.Manifest.Sections.SingleOrDefault(value => value.Name == "gameplay/tie_groups");
        var groupBytes = projectGroups ?? (groups is null ? null : UyaTieGroupsWriter.Remap(
            await File.ReadAllBytesAsync(
                ForgeProjectPersistence.ResolveRelativePath(opaqueRoot, groups.Blob), cancellationToken),
            sourceIndices));
        return new(groupBytes,
            UyaOcclusionMappingsWriter.RemapTiesBySourceIndex(
                occlusionSource,
                sourceIndices,
                occlusionIds,
                insertedBitIndex < 0 ? null : insertedBitIndex),
            insertedBitIndex < 0 ? null : insertedBitIndex);
    }

    private static async Task<UyaStaticReferenceOutputs?> PrepareMobyGroupsAsync(
        OpaqueContentManifest opaque,
        string opaqueRoot,
        ForgeProjectWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var section = opaque.Sections.SingleOrDefault(value => value.Name == "gameplay/moby_groups");
        var ordered = UyaStaticLayerStore.OrderedEntities(workspace, BakeLayerId.Mobys);
        var imported = workspace.Content.ImportBatches.SelectMany(value => value.Added)
            .Where(value => value.StartsWith("entity:", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var sourceIndices = ordered.Select(value =>
            value.Provenance?.Level == workspace.Manifest.BaseLevel.Level
                && !imported.Contains($"entity:{value.EntityId}")
                ? value.Provenance.SourceIndex
                : -1).ToArray();
        var groups = section is null
            ? new List<List<int>>()
            : UyaTieGroupsReader.Read(UyaTieGroupsWriter.Remap(
                    await File.ReadAllBytesAsync(
                        ForgeProjectPersistence.ResolveRelativePath(opaqueRoot, section.Blob), cancellationToken),
                    sourceIndices))
                .Groups.Select(value => value.ToList()).ToList();
        var additions = false;
        for (var targetIndex = 0; targetIndex < ordered.Length; targetIndex++)
        {
            if (sourceIndices[targetIndex] >= 0) continue;
            var group = UyaMobyInstancesReader.ReadInstance(ordered[targetIndex].Source!.RawRecord).Group;
            if (group < 0) continue;
            if (group >= ProjectGroupSchema.MaximumGroupCount)
                throw new InvalidDataException(
                    $"UYA Moby group {group} exceeds the supported group limit.");
            while (groups.Count <= group) groups.Add([]);
            if (!groups[group].Contains(targetIndex)) groups[group].Add(targetIndex);
            additions = true;
        }
        if (section is null && !additions) return null;
        if (!additions && sourceIndices.SequenceEqual(Enumerable.Range(0, sourceIndices.Length))) return null;
        return new(UyaTieGroupsWriter.Write(groups), null, null);
    }

    private static ProjectEntity[] SourceEntities(ForgeProjectWorkspace workspace, string section) =>
        workspace.Content.Entities.Where(value => value.Provenance?.Section == section).ToArray();

    internal static byte[]? PrepareProjectGroups(ForgeProjectWorkspace workspace, BakeLayerId layer)
    {
        var kind = layer switch
        {
            BakeLayerId.Mobys => ProjectEntityKind.Moby,
            BakeLayerId.Ties => ProjectEntityKind.Tie,
            BakeLayerId.Shrubs => ProjectEntityKind.Shrub,
            _ => throw new ArgumentOutOfRangeException(nameof(layer)),
        };
        var table = workspace.Content.MapGroups.SingleOrDefault(value => value.MemberKind == kind);
        if (table is null) return null;
        var ordered = UyaStaticLayerStore.OrderedEntities(workspace, layer);
        var indexes = ordered.Select((value, index) => (value.EntityId, index))
            .ToDictionary(value => value.EntityId, value => value.index);
        var groups = table.Groups.Select((group, groupIndex) => group.Members.Select(member =>
            indexes.TryGetValue(member, out var index)
                ? index
                : throw new InvalidDataException(
                    $"Project {kind} map group {groupIndex} references a disabled or missing entity {member}.")).ToArray())
            .ToArray();
        if (kind == ProjectEntityKind.Moby)
        {
            var membership = table.Groups.SelectMany((group, groupIndex) =>
                    group.Members.Select(member => (member, groupIndex)))
                .ToDictionary(value => value.member, value => value.groupIndex);
            foreach (var entity in ordered)
            {
                var actual = UyaMobyInstancesReader.ReadInstance(entity.Source!.RawRecord).Group;
                var expected = membership.GetValueOrDefault(entity.EntityId, -1);
                if (actual != expected)
                    throw new InvalidDataException(
                        $"Project Moby {entity.EntityId} names map group {actual}, not {expected}.");
            }
        }
        return UyaTieGroupsWriter.Write(groups);
    }

    private static int ResolveSourceIndex(
        ProjectEntity entity,
        IReadOnlyList<ProjectEntity> source,
        int sourceLevel)
    {
        if (entity.Provenance is { } provenance)
            return provenance.Level == sourceLevel ? provenance.SourceIndex : -1;
        if (entity.Source?.SourceIndex is { } sourceIndex) return sourceIndex;
        var matches = source.Where(value => value.Source is not null && entity.Source is not null
                && value.Source.ClassId == entity.Source.ClassId
                && value.Source.RawRecord.SequenceEqual(entity.Source.RawRecord))
            .Select(value => value.Provenance!.SourceIndex).Distinct().ToArray();
        return matches.Length == 1 ? matches[0] : -1;
    }
}

internal sealed record UyaStaticReferenceOutputs(
    byte[]? Groups,
    byte[]? Occlusion,
    int? AlwaysVisibleBitIndex);

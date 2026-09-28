using System.Buffers.Binary;
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
        var opaque = await OpaqueContentStore.InspectAsync(projectRoot, cancellationToken);
        if (opaque.Manifest is null) return null;
        if (!opaque.IsValid) throw new InvalidDataException(string.Join(' ', opaque.Blockers));
        var opaqueRoot = ForgeProjectPersistence.ResolveRelativePath(projectRoot, OpaqueContentStore.RelativeRootPath);
        if (layer == BakeLayerId.Shrubs)
        {
            var shrubGroups = opaque.Manifest.Sections.SingleOrDefault(value => value.Name == "gameplay/shrub_groups");
            if (shrubGroups is null) return null;
            var source = SourceEntities(workspace, "gameplay/core/shrub_instances");
            var indices = UyaStaticLayerStore.OrderedEntities(workspace, layer)
                .Select(value => ResolveSourceIndex(value, source)).ToArray();
            if (indices.SequenceEqual(Enumerable.Range(0, source.Length))) return null;
            return new(UyaTieGroupsWriter.Remap(
                await File.ReadAllBytesAsync(
                    ForgeProjectPersistence.ResolveRelativePath(opaqueRoot, shrubGroups.Blob), cancellationToken),
                indices), null, null);
        }

        var occlusion = opaque.Manifest.Sections.SingleOrDefault(value => value.Name == "gameplay/occlusion");
        if (occlusion is null) return null;
        var occlusionSource = await File.ReadAllBytesAsync(
            ForgeProjectPersistence.ResolveRelativePath(opaqueRoot, occlusion.Blob), cancellationToken);
        var mappings = UyaOcclusionMappingsReader.Read(occlusionSource);
        var ties = UyaStaticLayerStore.OrderedEntities(workspace, BakeLayerId.Ties);
        var sourceTies = SourceEntities(workspace, "gameplay/core/tie_instances");
        var sourceIndices = ties.Select(value => ResolveSourceIndex(value, sourceTies)).ToArray();
        if (sourceIndices.Any(value => value >= mappings.Ties.Count))
            throw new InvalidDataException("UYA tie source index has no corresponding occlusion mapping.");
        var insertedBitIndex = sourceIndices.Any(value => value < 0)
            ? Enumerable.Range(0, 1024).FirstOrDefault(index => !mappings.Tfrags.Concat(mappings.Ties)
                .Concat(mappings.Mobys).Any(value => value.BitIndex == index), -1)
            : -1;
        if (sourceIndices.Any(value => value < 0) && insertedBitIndex < 0)
            throw new InvalidDataException("UYA occlusion mapping has no free visibility bit for an inserted tie.");
        var occlusionIds = ties.Select(value => BinaryPrimitives.ReadInt32LittleEndian(
            value.Source!.RawRecord.AsSpan(UyaTieInstancesReader.OcclusionIdOffset))).ToArray();
        if (sourceIndices.SequenceEqual(Enumerable.Range(0, mappings.Ties.Count))) return null;
        var groups = opaque.Manifest.Sections.SingleOrDefault(value => value.Name == "gameplay/tie_groups");
        var groupBytes = groups is null ? null : UyaTieGroupsWriter.Remap(
            await File.ReadAllBytesAsync(
                ForgeProjectPersistence.ResolveRelativePath(opaqueRoot, groups.Blob), cancellationToken),
            sourceIndices);
        return new(groupBytes,
            UyaOcclusionMappingsWriter.RemapTies(
                occlusionSource, occlusionIds, insertedBitIndex < 0 ? null : insertedBitIndex),
            insertedBitIndex < 0 ? null : insertedBitIndex);
    }

    private static ProjectEntity[] SourceEntities(ForgeProjectWorkspace workspace, string section) =>
        workspace.Content.Entities.Where(value => value.Provenance?.Section == section).ToArray();

    private static int ResolveSourceIndex(ProjectEntity entity, IReadOnlyList<ProjectEntity> source)
    {
        if (entity.Provenance is not null) return entity.Provenance.SourceIndex;
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

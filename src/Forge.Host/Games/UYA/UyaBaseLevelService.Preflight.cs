using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static partial class UyaBaseLevelService
{
    private static readonly UyaPreflightLayerSummaryCache LayerSummaries = new();

    public static UyaProjectPreflight Preflight(
        Stream iso, AssetCatalogStore catalog, int level, CancellationToken cancellationToken)
    {
        var prepared = Prepare(iso, catalog, level, cancellationToken);
        var baseInstanceCount = LayerSummaries.GetOrCreate(prepared.Wad, () =>
        {
            var entities = new List<ProjectEntity>();
            var layers = PrepareLayers(prepared.Package, entities, level, cancellationToken);
            return checked(entities.Count + layers.Tfrags.Entities.Count);
        }, cancellationToken);
        return new(level,
            checked(prepared.Entities.Count + baseInstanceCount),
            checked(prepared.Entities.Count(entity => entity.Asset is not null) + baseInstanceCount),
            prepared.ModelLessInstances, prepared.MissingInstances, prepared.MissingClasses,
            CreateWarnings(prepared.MissingInstances, prepared.MissingClasses, prepared.FallbackTransforms));
    }

    internal static IReadOnlyList<string> CreateWarnings(int missingInstances, int missingClasses, int fallbackTransforms)
    {
        var warnings = new List<string>();
        if (missingInstances > 0)
            warnings.Add($"{missingInstances} instances across {missingClasses} model classes are absent from the global catalog and will use placeholders.");
        if (fallbackTransforms > 0)
            warnings.Add($"{fallbackTransforms} tie or shrub transforms could not be decomposed and will use identity rotation and scale.");
        return warnings;
    }

    private static PreparedBase Prepare(
        Stream iso, AssetCatalogStore catalog, int level, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var levelWad = LevelArchiveReader.ExtractPrimary(GameId.UYA, iso, level);

        cancellationToken.ThrowIfCancellationRequested();

        var package = UyaLevelWadUnpacker.Unpack(levelWad);

        cancellationToken.ThrowIfCancellationRequested();

        var files = package.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var mobyBytes = Required(files, "gameplay/core/moby_instances.bin", level);
        var tieBytes = Required(files, "gameplay/core/tie_instances.bin", level);
        var shrubBytes = Required(files, "gameplay/core/shrub_instances.bin", level);
        var gameplay = UyaGameplayBlockReader.ReadCore(
            Required(files, "gameplay/gameplay_core.bin", level));
        var mobyTable = mobyBytes.Length == 0
            ? new UyaMobyInstances(0, 0, 0, 0, [], [])
            : UyaMobyInstancesReader.Read(mobyBytes);
        var mobys = mobyTable.Instances;
        var tieTable = tieBytes.Length == 0
            ? new UyaTieInstances(0, [0, 0, 0], [], [])
            : UyaTieInstancesReader.Read(tieBytes);
        var shrubTable = shrubBytes.Length == 0
            ? new UyaShrubInstances(0, [0, 0, 0], [], [])
            : UyaShrubInstancesReader.Read(shrubBytes);
        var ties = tieTable.Instances;
        var shrubs = shrubTable.Instances;

        var source = UyaLevelWadRenderPackageBuilder.ReadAssetSourceFiles(package.Files);
        var header = LevelAssetReader.ReadHeader(source.HeaderBytes);

        var mobyClasses = ModelClasses(source.HeaderBytes, header.MobyModelOffset, header.MobyModelCount);
        var tieClasses = ModelClasses(source.HeaderBytes, header.TieModelOffset, header.TieModelCount);
        var shrubClasses = LevelAssetReader.ReadShrubDefinitions(source.HeaderBytes, header.ShrubModelOffset, header.ShrubModelCount)
            .Where(model => model.ModelOffset > 0).Select(model => model.ModelId).ToHashSet();
        var mobyAssets = AssetLookup(catalog, AssetKind.Moby, "moby", level);
        var tieAssets = AssetLookup(catalog, AssetKind.Tie, "tie", level);
        var shrubAssets = AssetLookup(catalog, AssetKind.Shrub, "shrub", level);

        var geometry = gameplay.Geometry;
        var lighting = gameplay.Lighting;
        var cameras = gameplay.Blocks.FirstOrDefault(value => value.CameraInstances is not null)?.CameraInstances?.Instances ?? [];
        var sounds = gameplay.Blocks.FirstOrDefault(value => value.SoundInstances is not null)?.SoundInstances?.Instances ?? [];
        var cameraCollision = gameplay.Blocks.FirstOrDefault(value => value.CameraCollisionGrid is not null)
            ?.CameraCollisionGrid?.Primitives ?? [];
        var entities = new List<ProjectEntity>(
            mobys.Count + ties.Count + shrubs.Count + geometry.Cuboids.Length + geometry.Spheres.Length
            + geometry.Cylinders.Length + geometry.Pills.Length + geometry.Splines.Length
            + geometry.GrindPaths.Length + geometry.Areas.Length + lighting.DirectionalLights.Length
            + lighting.PointLights.Lights.Length + lighting.EnvironmentSamplePoints.Length
            + lighting.EnvironmentTransitions.Length + cameras.Count + sounds.Count);
        for (var index = 0; index < mobys.Count; index++)
        {
            var instance = mobys[index];
            entities.Add(new(
                EntityId.New(),
                $"Moby 0x{instance.ClassId:X4} #{index}",
                "mobys",
                new(
                    new(instance.Position.X, instance.Position.Y, instance.Position.Z),
                    FromZyxEuler(instance.Rotation),
                    new(instance.Scale, instance.Scale, instance.Scale)),
                Reference(mobyAssets, instance.ClassId, AssetKind.Moby),
                new("UYA", level, "gameplay/core/moby_instances", index),
                Source: new(instance.ClassId, mobyBytes.AsSpan(
                    UyaMobyInstancesReader.HeaderSize + index * UyaMobyInstancesReader.RecordSize,
                    UyaMobyInstancesReader.RecordSize).ToArray(), !mobyClasses.Contains(instance.ClassId))));
        }

        var fallbackTransforms = 0;
        for (var index = 0; index < ties.Count; index++)
        {
            var tie = ties[index];
            entities.Add(CreateStaticEntity(tie.ClassId, tie.Transform, tie.RawBytes,
                index, level, "Tie", "ties", "gameplay/core/tie_instances", AssetKind.Tie, tieAssets, ref fallbackTransforms) with
            {
                TieLighting = new(tie.DirectionalLights, lighting.TieAmbientRgbas[index]),
            });
        }
        for (var index = 0; index < shrubs.Count; index++)
            entities.Add(CreateStaticEntity(shrubs[index].ClassId, shrubs[index].Transform, shrubs[index].RawBytes,
                index, level, "Shrub", "shrubs", "gameplay/core/shrub_instances", AssetKind.Shrub, shrubAssets, ref fallbackTransforms));
        AddGeometryEntities(entities, geometry, cameraCollision, level, ref fallbackTransforms);
        AddLightingEntities(entities, lighting, level, ref fallbackTransforms);
        AddEnvironmentInstances(entities, cameras, sounds, level, ref fallbackTransforms);

        cancellationToken.ThrowIfCancellationRequested();
        var missing = CountMissing(mobys.Select(value => value.ClassId), mobyClasses, mobyAssets)
            + CountMissing(ties.Select(value => value.ClassId), tieClasses, tieAssets)
            + CountMissing(shrubs.Select(value => value.ClassId), shrubClasses, shrubAssets);
        var missingClasses = MissingClasses(mobys.Select(value => value.ClassId), mobyClasses, mobyAssets)
            + MissingClasses(ties.Select(value => value.ClassId), tieClasses, tieAssets)
            + MissingClasses(shrubs.Select(value => value.ClassId), shrubClasses, shrubAssets);
        cancellationToken.ThrowIfCancellationRequested();
        return new(levelWad, package, source, header, gameplay, mobyTable, tieTable, shrubTable,
            mobyAssets, entities,
            mobys.Count(instance => !mobyClasses.Contains(instance.ClassId)),
            missing, missingClasses, fallbackTransforms);
    }

    private static PreparedLayers PrepareLayers(
        UyaLevelWadPackage package, List<ProjectEntity> entities, int level, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var baseLayers = UyaBaseLayerService.Extract(package);
        AddSkyShells(entities, baseLayers, level);
        AddCollisionPieces(entities, baseLayers, level);

        cancellationToken.ThrowIfCancellationRequested();

        var tfrags = UyaTfragProjectService.Create(baseLayers, level, cancellationToken);
        return new(baseLayers, tfrags);
    }

    private sealed record PreparedLayers(IReadOnlyList<UyaBaseLayerPayload> BaseLayers, UyaTfragProjectImport Tfrags);

    private sealed record PreparedBase(
        byte[] Wad,
        UyaLevelWadPackage Package,
        UyaLevelAssetSourceFiles Source,
        LevelAssetHeader Header,
        UyaGameplayBlocks Gameplay,
        UyaMobyInstances Mobys,
        UyaTieInstances Ties,
        UyaShrubInstances Shrubs,
        IReadOnlyDictionary<int, AssetCatalogEntry> MobyAssets,
        List<ProjectEntity> Entities,
        int ModelLessInstances,
        int MissingInstances,
        int MissingClasses,
        int FallbackTransforms);
}

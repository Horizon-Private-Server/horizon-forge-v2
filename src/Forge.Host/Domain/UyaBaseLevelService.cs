using System.Numerics;
using RatchetPs2.Games.DL.Level;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Games.UYA.Level;

namespace Forge.Host.Domain;

internal static class UyaBaseLevelService
{
    public static UyaBaseLevelData Read(Stream iso, AssetCatalogStore catalog, int level)
    {
        var levelWad = UyaLooseLevelWadExtractor.ExtractPrimary(iso, level).Bytes;
        var package = UyaLevelWadUnpacker.Unpack(levelWad);
        var files = package.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var mobyBytes = Required(files, "gameplay/core/moby_instances.bin", level);
        var tieBytes = Required(files, "gameplay/core/tie_instances.bin", level);
        var shrubBytes = Required(files, "gameplay/core/shrub_instances.bin", level);
        var mobys = mobyBytes.Length == 0 ? [] : UyaMobyInstancesReader.Read(mobyBytes).Instances;
        var ties = tieBytes.Length == 0 ? [] : UyaTieInstancesReader.Read(tieBytes).Instances;
        var shrubs = shrubBytes.Length == 0 ? [] : UyaShrubInstancesReader.Read(shrubBytes).Instances;

        var source = UyaLevelWadRenderPackageBuilder.ReadAssetSourceFiles(package.Files);
        var header = DlAssetReader.ReadHeader(source.HeaderBytes);
        var mobyClasses = ModelClasses(source.HeaderBytes, header.MobyModelOffset, header.MobyModelCount);
        var tieClasses = ModelClasses(source.HeaderBytes, header.TieModelOffset, header.TieModelCount);
        var shrubClasses = DlAssetReader.ReadShrubDefinitions(source.HeaderBytes, header.ShrubModelOffset, header.ShrubModelCount)
            .Where(model => model.ModelOffset > 0).Select(model => model.ModelId).ToHashSet();
        var mobyAssets = AssetLookup(catalog, AssetKind.Moby, "moby", level);
        var tieAssets = AssetLookup(catalog, AssetKind.Tie, "tie", level);
        var shrubAssets = AssetLookup(catalog, AssetKind.Shrub, "shrub", level);

        var entities = new List<ProjectEntity>(mobys.Count + ties.Count + shrubs.Count);
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
                    UyaMobyInstancesReader.RecordSize).ToArray())));
        }

        var fallbackTransforms = 0;
        for (var index = 0; index < ties.Count; index++)
            entities.Add(CreateStaticEntity(ties[index].ClassId, ties[index].Transform, ties[index].RawBytes,
                index, level, "Tie", "ties", "gameplay/core/tie_instances", AssetKind.Tie, tieAssets, ref fallbackTransforms));
        for (var index = 0; index < shrubs.Count; index++)
            entities.Add(CreateStaticEntity(shrubs[index].ClassId, shrubs[index].Transform, shrubs[index].RawBytes,
                index, level, "Shrub", "shrubs", "gameplay/core/shrub_instances", AssetKind.Shrub, shrubAssets, ref fallbackTransforms));

        var missing = CountMissing(mobys.Select(value => value.ClassId), mobyClasses, mobyAssets)
            + CountMissing(ties.Select(value => value.ClassId), tieClasses, tieAssets)
            + CountMissing(shrubs.Select(value => value.ClassId), shrubClasses, shrubAssets);
        var missingClasses = MissingClasses(mobys.Select(value => value.ClassId), mobyClasses, mobyAssets)
            + MissingClasses(ties.Select(value => value.ClassId), tieClasses, tieAssets)
            + MissingClasses(shrubs.Select(value => value.ClassId), shrubClasses, shrubAssets);
        return new(
            entities,
            mobys.Count + ties.Count + shrubs.Count,
            entities.Count(entity => entity.Asset is not null),
            mobys.Count(instance => !mobyClasses.Contains(instance.ClassId)),
            missing,
            missingClasses,
            fallbackTransforms);
    }

    private static ProjectEntity CreateStaticEntity(
        int classId,
        UyaInstanceTransform sourceTransform,
        byte[] rawRecord,
        int index,
        int level,
        string familyName,
        string layer,
        string section,
        AssetKind kind,
        IReadOnlyDictionary<int, AssetCatalogEntry> assets,
        ref int fallbackTransforms)
    {
        var transform = Decompose(sourceTransform);
        if (transform is null)
        {
            fallbackTransforms++;
            transform = new(
                new(sourceTransform.Position.X, sourceTransform.Position.Y, sourceTransform.Position.Z),
                new(0, 0, 0, 1),
                new(1, 1, 1));
        }
        return new(
            EntityId.New(),
            $"{familyName} 0x{classId:X4} #{index}",
            layer,
            transform,
            Reference(assets, classId, kind),
            new("UYA", level, section, index),
            Source: new(classId, rawRecord));
    }

    private static ProjectTransform? Decompose(UyaInstanceTransform value)
    {
        var basisX = new Vector3(value.BasisX.X, value.BasisX.Y, value.BasisX.Z);
        var basisY = new Vector3(value.BasisY.X, value.BasisY.Y, value.BasisY.Z);
        var basisZ = new Vector3(value.BasisZ.X, value.BasisZ.Y, value.BasisZ.Z);
        var scale = new Vector3(basisX.Length(), basisY.Length(), basisZ.Length());
        var matrix = new Matrix4x4(
            value.BasisX.X, value.BasisX.Y, value.BasisX.Z, 0,
            value.BasisY.X, value.BasisY.Y, value.BasisY.Z, 0,
            value.BasisZ.X, value.BasisZ.Y, value.BasisZ.Z, 0,
            value.Position.X, value.Position.Y, value.Position.Z, 1);
        if (matrix.GetDeterminant() < 0) scale.X = -scale.X;
        var position = new Vector3(value.Position.X, value.Position.Y, value.Position.Z);
        if (scale.X == 0 || scale.Y == 0 || scale.Z == 0 || !Finite(scale) || !Finite(position)) return null;
        var rotation = Quaternion.CreateFromRotationMatrix(new(
            basisX.X / scale.X, basisX.Y / scale.X, basisX.Z / scale.X, 0,
            basisY.X / scale.Y, basisY.Y / scale.Y, basisY.Z / scale.Y, 0,
            basisZ.X / scale.Z, basisZ.Y / scale.Z, basisZ.Z / scale.Z, 0,
            0, 0, 0, 1));
        if (!float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y)
            || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W)
            || rotation.LengthSquared() == 0) return null;
        rotation = Quaternion.Normalize(rotation);
        return new(
            new(position.X, position.Y, position.Z),
            new(rotation.X, rotation.Y, rotation.Z, rotation.W),
            new(scale.X, scale.Y, scale.Z));
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static ProjectQuaternion FromZyxEuler(UyaVector3 rotation)
    {
        var c1 = MathF.Cos(rotation.X / 2);
        var c2 = MathF.Cos(rotation.Y / 2);
        var c3 = MathF.Cos(rotation.Z / 2);
        var s1 = MathF.Sin(rotation.X / 2);
        var s2 = MathF.Sin(rotation.Y / 2);
        var s3 = MathF.Sin(rotation.Z / 2);
        return new(
            s1 * c2 * c3 - c1 * s2 * s3,
            c1 * s2 * c3 + s1 * c2 * s3,
            c1 * c2 * s3 - s1 * s2 * c3,
            c1 * c2 * c3 + s1 * s2 * s3);
    }

    private static HashSet<int> ModelClasses(byte[] header, int offset, int count) =>
        DlAssetReader.ReadModelDefinitions(header, offset, count)
            .Where(model => model.ModelOffset > 0).Select(model => model.ModelId).ToHashSet();

    private static IReadOnlyDictionary<int, AssetCatalogEntry> AssetLookup(
        AssetCatalogStore catalog,
        AssetKind kind,
        string aliasPrefix,
        int level)
    {
        var result = new Dictionary<int, AssetCatalogEntry>();
        foreach (var asset in catalog.Query(new(Kind: kind, Game: "UYA", Level: $"level{level:00}", Limit: AssetCatalogStore.MaxQueryLimit)))
        {
            var prefix = $"{aliasPrefix}:";
            var alias = asset.Aliases.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal)
                && !value.StartsWith($"{prefix}0x", StringComparison.Ordinal));
            if (alias is not null && int.TryParse(alias.AsSpan(prefix.Length), out var classId)) result.TryAdd(classId, asset);
        }
        return result;
    }

    private static ProjectAssetReference? Reference(
        IReadOnlyDictionary<int, AssetCatalogEntry> assets,
        int classId,
        AssetKind kind) => assets.TryGetValue(classId, out var asset) ? new(asset.Id, kind) : null;

    private static int CountMissing(
        IEnumerable<int> instances,
        IReadOnlySet<int> modelClasses,
        IReadOnlyDictionary<int, AssetCatalogEntry> assets) =>
        instances.Count(classId => modelClasses.Contains(classId) && !assets.ContainsKey(classId));

    private static int MissingClasses(
        IEnumerable<int> instances,
        IReadOnlySet<int> modelClasses,
        IReadOnlyDictionary<int, AssetCatalogEntry> assets) =>
        instances.Where(classId => modelClasses.Contains(classId) && !assets.ContainsKey(classId)).Distinct().Count();

    private static byte[] Required(
        IReadOnlyDictionary<string, RatchetPs2.Core.Wad.Models.PackedFile> files,
        string path,
        int level) => files.TryGetValue(path, out var file)
            ? file.Bytes
            : throw new InvalidDataException($"UYA level {level} does not contain readable {Path.GetFileNameWithoutExtension(path)} data.");
}

internal sealed record UyaBaseLevelData(
    IReadOnlyList<ProjectEntity> Entities,
    int SourceInstanceCount,
    int RenderableInstanceCount,
    int ModelLessInstanceCount,
    int MissingInstanceCount,
    int MissingClassCount,
    int FallbackTransformCount);

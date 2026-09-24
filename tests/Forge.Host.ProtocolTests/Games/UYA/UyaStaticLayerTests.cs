using Forge.Host.Games.UYA;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Host.Domain;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaStaticLayerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-static-bake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var tieBytes = CanonicalAsset(AssetKind.Tie, 0x11);
            var shrubBytes = CanonicalAsset(AssetKind.Shrub, 0x22);
            var mobyBytes = CanonicalAsset(AssetKind.Moby, 0x33);
            var tieAsset = await PutAsync(catalog, AssetKind.Tie, 0x0200, tieBytes);
            var shrubAsset = await PutAsync(catalog, AssetKind.Shrub, 0x0300, shrubBytes);
            var mobyAsset = await PutAsync(catalog, AssetKind.Moby, 0x0400, mobyBytes);
            var project = Path.Combine(root, "project");
            var hiddenTie = Entity("Hidden tie", "ties", tieAsset, 0x0200,
                UyaTieInstancesReader.RecordSize, new(Hidden: true), new(10, 20, 30), new(2, 3, 4), 0);
            var disabledTie = Entity("Disabled tie", "ties", tieAsset, 0x0200,
                UyaTieInstancesReader.RecordSize, new(Disabled: true), new(40, 50, 60), new(1, 1, 1), 1);
            var lockedShrub = Entity("Locked shrub", "shrubs", shrubAsset, 0x0300,
                UyaShrubInstancesReader.RecordSize, new(Locked: true), new(70, 80, 90), new(1, 2, 3), 0);
            var moby = MobyEntity(mobyAsset, 0x0400, new(100, 200, 300), 2, 0, new());
            var disabledMoby = MobyEntity(mobyAsset, 0x0400, new(400, 500, 600), 1, 1, new(Disabled: true));
            var modelLessMoby = ModelLessMobyEntity(0x0401, 2);
            await ForgeProjectWorkspace.CreateAsync(
                project,
                "Static bake",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 3, new string('a', 32), EntityVersion: 1),
                [hiddenTie, disabledTie, lockedShrub, moby, disabledMoby, modelLessMoby]);
            await WriteSourceAsync(project);

            var inputs = await UyaStaticLayerStore.CreateBakeInputsAsync(project, catalog);
            var plan = BakeLayerGraph.CreatePlan(Context(), inputs);
            var staging = await BakeStagingStore.OpenAsync(project);
            var tieSnapshot = await UyaStaticLayerStore.StageAsync(
                project, catalog, staging, Layer(plan, BakeLayerId.Ties));
            var shrubSnapshot = await UyaStaticLayerStore.StageAsync(
                project, catalog, staging, Layer(plan, BakeLayerId.Shrubs));
            var mobySnapshot = await UyaStaticLayerStore.StageAsync(
                project, catalog, staging, Layer(plan, BakeLayerId.Mobys));

            var bakedTies = UyaTieInstancesReader.Read(await File.ReadAllBytesAsync(
                Path.Combine(staging.RootPath, tieSnapshot.RelativePath, "instances.bin")));
            Equal(1, bakedTies.Count, "hidden tie included and disabled tie omitted");
            Equal(true, bakedTies.HeaderWords.SequenceEqual([7, 8, 9]), "tie header words preserved");
            Equal(true, bakedTies.TrailingBytes.SequenceEqual(new byte[] { 0xaa, 0xbb }), "tie trailing bytes preserved");
            Equal(0x0200, bakedTies.Instances[0].ClassId, "tie class resolved");
            Equal(new UyaVector4(10, 20, 30, 4), bakedTies.Instances[0].Transform.Position,
                "tie position and unknown W preserved");
            Near(2, bakedTies.Instances[0].Transform.BasisX.X, "tie X scale");
            Near(3, bakedTies.Instances[0].Transform.BasisY.Y, "tie Y scale");
            Near(4, bakedTies.Instances[0].Transform.BasisZ.Z, "tie Z scale");
            Equal(unchecked((int)0xAABBCCDD), BinaryPrimitives.ReadInt32LittleEndian(
                bakedTies.Instances[0].RawBytes.AsSpan(8)), "tie unknown record value preserved");

            var bakedShrubs = UyaShrubInstancesReader.Read(await File.ReadAllBytesAsync(
                Path.Combine(staging.RootPath, shrubSnapshot.RelativePath, "instances.bin")));
            Equal(1, bakedShrubs.Count, "locked shrub included");
            Equal(1234f, bakedShrubs.Instances[0].DrawDistance, "shrub draw distance preserved");

            var bakedMobys = UyaMobyInstancesReader.Read(await File.ReadAllBytesAsync(
                Path.Combine(staging.RootPath, mobySnapshot.RelativePath, "instances.bin")));
            Equal(2, bakedMobys.StaticCount, "renderable and model-less mobys included");
            Equal(0x0400, bakedMobys.Instances[0].ClassId, "moby class resolved");
            Equal(new UyaVector3(100, 200, 300), bakedMobys.Instances[0].Position, "moby position");
            Equal(2f, bakedMobys.Instances[0].Scale, "moby uniform scale");
            Equal(7, bakedMobys.Instances[0].PvarIndex, "moby pvar index preserved");
            Equal(0x0401, bakedMobys.Instances[1].ClassId, "model-less moby class preserved");

            var tieManifest = ReadManifest(Path.Combine(staging.RootPath, tieSnapshot.RelativePath, "manifest.json"));
            Equal(0, tieManifest.Definitions.Single().TargetIndex, "tie definition index");
            Equal(hiddenTie.EntityId, tieManifest.Instances.Single().EntityId, "tie entity target mapping");
            Equal(0, tieManifest.Instances.Single().TargetIndex, "tie instance index");
            Equal(true, File.ReadAllBytes(Path.Combine(staging.RootPath, tieSnapshot.RelativePath,
                tieManifest.Definitions.Single().Resource)).SequenceEqual(tieBytes), "tie resource staged");
            var mobyManifest = ReadManifest(Path.Combine(staging.RootPath, mobySnapshot.RelativePath, "manifest.json"));
            Equal(-1, mobyManifest.Instances.Single(value => value.EntityId == modelLessMoby.EntityId).DefinitionIndex,
                "model-less moby needs no model definition");

            var repeated = await UyaStaticLayerStore.StageAsync(
                project, catalog, staging, Layer(plan, BakeLayerId.Ties));
            Equal(tieSnapshot.OutputFingerprint, repeated.OutputFingerprint, "static output deterministic");

            await VerifyBadReferencesAsync(root, catalog, shrubAsset);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifyBadReferencesAsync(
        string root,
        AssetCatalogStore catalog,
        AssetCatalogEntry shrubAsset)
    {
        var project = Path.Combine(root, "bad-project");
        var missing = new ProjectAssetReference(AssetId.Compute(AssetKind.Tie, 0, "missing"u8), AssetKind.Tie);
        var incompatible = new ProjectAssetReference(shrubAsset.Id, AssetKind.Tie);
        await ForgeProjectWorkspace.CreateAsync(
            project,
            "Bad static bake",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
            new("UYA", "NTSC-U", "1.00", 3, new string('a', 32), EntityVersion: 1),
            [
                Entity("Missing tie", "ties", missing, 0x0200, UyaTieInstancesReader.RecordSize,
                    new(), new(0, 0, 0), new(1, 1, 1), 0),
                Entity("Wrong tie", "ties", incompatible, 0x0300, UyaTieInstancesReader.RecordSize,
                    new(), new(0, 0, 0), new(1, 1, 1), 1),
            ]);
        await WriteSourceAsync(project);
        var inputs = await UyaStaticLayerStore.CreateBakeInputsAsync(project, catalog);
        var ties = inputs.Single(value => value.Id == BakeLayerId.Ties);
        var shrubs = inputs.Single(value => value.Id == BakeLayerId.Shrubs);
        Equal(true, ties.Blockers!.Any(value => value.Contains("missing vanilla asset", StringComparison.Ordinal)),
            "dangling tie reference blocks ties");
        Equal(true, ties.Blockers!.Any(value => value.Contains("not compatible", StringComparison.Ordinal)),
            "incompatible tie reference blocks ties");
        Equal(0, shrubs.Blockers!.Count, "tie failures do not block shrubs");
    }

    private static ProjectEntity Entity(
        string name,
        string layer,
        AssetCatalogEntry asset,
        int classId,
        int recordSize,
        ProjectEntityState state,
        ProjectVector3 position,
        ProjectVector3 scale,
        int sourceIndex) =>
        Entity(name, layer, new ProjectAssetReference(asset.Id, asset.Kind),
            classId, recordSize, state, position, scale, sourceIndex);

    private static ProjectEntity Entity(
        string name,
        string layer,
        ProjectAssetReference asset,
        int classId,
        int recordSize,
        ProjectEntityState state,
        ProjectVector3 position,
        ProjectVector3 scale,
        int sourceIndex)
    {
        var raw = new byte[recordSize];
        BinaryPrimitives.WriteInt32LittleEndian(raw, classId);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(8), unchecked((int)0xAABBCCDD));
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(4), 1234);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(0x1c), 1);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(0x2c), 2);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(0x3c), 3);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(0x4c), 4);
        return new(
            EntityId.New(),
            name,
            layer,
            new(position, new(0, 0, 0, 1), scale),
            asset,
            new("UYA", 3, $"gameplay/core/{layer.TrimEnd('s')}_instances", sourceIndex),
            state,
            new(classId, raw));
    }

    private static ProjectEntity MobyEntity(
        AssetCatalogEntry asset,
        int classId,
        ProjectVector3 position,
        float scale,
        int sourceIndex,
        ProjectEntityState state)
    {
        var raw = new byte[UyaMobyInstancesReader.RecordSize];
        BinaryPrimitives.WriteInt32LittleEndian(raw, UyaMobyInstancesReader.RecordSize);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x10), 0x1234);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x28), classId);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(0x2c), 1);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x68), 7);
        return new(
            EntityId.New(),
            $"Moby 0x{classId:X4}",
            "mobys",
            new(position, new(0, 0, 0, 1), new(scale, scale, scale)),
            new(asset.Id, asset.Kind),
            new("UYA", 3, "gameplay/core/moby_instances", sourceIndex),
            state,
            Source: new(classId, raw));
    }

    private static ProjectEntity ModelLessMobyEntity(int classId, int sourceIndex)
    {
        var raw = new byte[UyaMobyInstancesReader.RecordSize];
        BinaryPrimitives.WriteInt32LittleEndian(raw, UyaMobyInstancesReader.RecordSize);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x28), classId);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(0x2c), 1);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0x68), -1);
        return new(
            EntityId.New(),
            $"Moby 0x{classId:X4}",
            "mobys",
            ProjectTransform.Identity,
            null,
            new("UYA", 3, "gameplay/core/moby_instances", sourceIndex),
            Source: new(classId, raw, ModelLess: true));
    }

    private static async Task<AssetCatalogEntry> PutAsync(
        AssetCatalogStore catalog,
        AssetKind kind,
        int classId,
        byte[] bytes) =>
        await catalog.PutAsync(kind, UyaAssetImportService.CanonicalFormatVersion, bytes, new(
            "test",
            new("UYA", "NTSC-U", "1.00", "level03", "level_wad/assets/asset_wad.bin", 0, new string('a', 32)),
            [$"{kind.ToString().ToLowerInvariant()}:{classId}", $"{kind.ToString().ToLowerInvariant()}:0x{classId:X4}"],
            ["vanilla", "game:UYA", "level:03"]));

    private static byte[] CanonicalAsset(AssetKind kind, byte modelByte)
    {
        var definitionLength = kind == AssetKind.Shrub ? 0x30 : 0x20;
        var bytes = new byte[19 + definitionLength];
        "HFUYA1"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(6), definitionLength);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10 + definitionLength), 1);
        bytes[14 + definitionLength] = modelByte;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(15 + definitionLength), 0);
        return bytes;
    }

    private static Task WriteSourceAsync(string project) => UyaStaticLayerStore.WriteSourceAsync(
        project,
        new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)),
        [
            new(BakeLayerId.Ties, UyaTieInstancesReader.RecordSize, [7, 8, 9], [0xaa, 0xbb]),
            new(BakeLayerId.Shrubs, UyaShrubInstancesReader.RecordSize, [10, 11, 12], [0xcc]),
            new(BakeLayerId.Mobys, UyaMobyInstancesReader.RecordSize, [400, 13, 14], [0xdd]),
        ]);

    private static UyaStaticBakeManifest ReadManifest(string path) =>
        JsonSerializer.Deserialize<UyaStaticBakeManifest>(File.ReadAllBytes(path), JsonOptions)
        ?? throw new InvalidDataException("Static bake manifest is empty.");

    private static BakeFingerprintContext Context() => new(
        new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), "translator-1", "baker-1");

    private static BakeLayerPlan Layer(BakePlan plan, BakeLayerId layer) =>
        plan.Layers.Single(value => value.Layer == layer);

    private static void Near(float expected, float actual, string context)
    {
        if (MathF.Abs(expected - actual) > 0.0001f)
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}

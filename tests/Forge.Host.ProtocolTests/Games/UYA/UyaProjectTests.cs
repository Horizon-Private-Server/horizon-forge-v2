using Forge.Host.Games.UYA;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Host.Domain;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Games.UYA.Level;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaProjectTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-uya-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var global = await catalog.PutAsync(
                AssetKind.Moby,
                UyaAssetImportService.CanonicalFormatVersion,
                CanonicalAsset(AssetKind.Moby, 0x11),
                new(
                    "test-importer",
                    new("UYA", "NTSC-U", "1.00", "level03", "level_wad/assets/asset_wad.bin", 0, new string('a', 32)),
                    ["moby:100", "moby:0x0064"],
                    ["vanilla", "game:UYA", "level:03"]));
            var tieAsset = await catalog.PutAsync(
                AssetKind.Tie, UyaAssetImportService.CanonicalFormatVersion, CanonicalAsset(AssetKind.Tie, 0x22),
                new("test-importer", new("UYA", "NTSC-U", "1.00", "level03", "level_wad/assets/asset_wad.bin", 0, new string('a', 32)),
                    ["tie:200", "tie:0x00C8"], ["vanilla", "game:UYA", "level:03"]));
            var shrubAsset = await catalog.PutAsync(
                AssetKind.Shrub, UyaAssetImportService.CanonicalFormatVersion, CanonicalAsset(AssetKind.Shrub, 0x33),
                new("test-importer", new("UYA", "NTSC-U", "1.00", "level03", "level_wad/assets/asset_wad.bin", 0, new string('a', 32)),
                    ["shrub:300", "shrub:0x012C"], ["vanilla", "game:UYA", "level:03"]));
            var iso = CreateIso();
            var options = UyaProjectService.GetCreationOptions(new MemoryStream(iso, writable: false));
            Equal(true, options.Levels.SequenceEqual([3]), "base level options");
            Equal(0, options.Warnings.Count, "base coverage warning");
            var preflight = UyaProjectService.Preflight(new MemoryStream(iso, writable: false), catalog, 3);
            Equal(4, preflight.SourceInstanceCount, "source instance count");
            Equal(3, preflight.RenderableInstanceCount, "renderable instance count");
            Equal(1, preflight.ModelLessInstanceCount, "model-less instance count");
            Equal(0, preflight.MissingAssetInstanceCount, "missing asset instance count");
            Equal(0, preflight.MissingClassCount, "missing class count");
            Equal(0, preflight.Warnings.Count, "preflight warnings");

            var projectPath = Path.Combine(root, "project");
            var request = new UyaProjectCreationRequest(
                "synthetic.iso", catalog.RootPath, projectPath, "Base project", new string('a', 32), "1.00", 3, true);
            var descriptor = await UyaProjectService.CreateValidatedAsync(
                new MemoryStream(iso, writable: false), catalog, request);
            Equal(4, descriptor.EntityCount, "base entity count");
            Equal(0, descriptor.MissingAssetCount, "base missing assets");

            var opaque = await OpaqueContentStore.InspectAsync(projectPath);
            Equal(true, opaque.IsValid, "opaque content captured");
            var code = opaque.Manifest!.Sections.Single(section => section.Name == "level-data/code-overlay");
            Equal("5f78c33274e43fa9de5659265c1d917e25c03722dcb0b8d27db8d5feaa813953", code.Checksum,
                "code overlay golden checksum");
            Equal("level_wad/level_data.wad", code.Placement.Container, "code overlay container");
            Equal(0, code.Placement.HeaderOffset, "code overlay header slot");
            Equal(0x80L, code.Placement.Offset, "code overlay source offset");
            Equal(4L, code.Placement.Length, "code overlay source length");
            var baseInspection = await UyaBaseLayerStore.InspectAsync(projectPath, catalog);
            Equal(true, baseInspection.IsValid, "target-native base layers captured");
            Equal(5, baseInspection.Manifest!.Layers.Sum(layer => layer.Assets.Count), "base layer asset count");
            Equal(1, baseInspection.Manifest.Layers.Single(layer => layer.Layer == BakeLayerId.Lighting).Assets.Count,
                "native lighting asset count");
            Equal(false, opaque.Manifest.Sections.Any(section => section.Name == "gameplay/directional_lights"),
                "lighting is not duplicated as opaque content");
            var opaqueInput = await OpaqueContentStore.CreateBakeInputAsync(projectPath);
            var baseInputs = await UyaBaseLayerStore.CreateBakeInputsAsync(projectPath, catalog);
            var authoredInputs = baseInputs.Append(opaqueInput).ToDictionary(input => input.Id);
            var bakeInputs = Enum.GetValues<BakeLayerId>().Select(layer => authoredInputs.GetValueOrDefault(layer)
                ?? new BakeLayerInput(layer, System.Text.Encoding.UTF8.GetBytes(layer.ToString()), [], ReadOnlyMemory<byte>.Empty))
                .ToArray();
            var bakeContext = new BakeFingerprintContext(
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), "translator-1", "baker-1");
            var validation = await UyaBakeValidationService.PreflightAsync(
                projectPath, catalog, bakeContext);
            Equal(true, validation.CanBake,
                "complete project passes structured bake preflight: "
                + string.Join(" | ", validation.Diagnostics.Select(value => value.Cause)));
            var bakePlan = BakeLayerGraph.CreatePlan(bakeContext, bakeInputs);
            var staging = await BakeStagingStore.OpenAsync(projectPath);
            var stagedOpaque = await OpaqueContentStore.StageAsync(
                projectPath,
                staging,
                bakePlan.Layers.Single(layer => layer.Layer == BakeLayerId.Opaque));
            foreach (var section in opaque.Manifest.Sections)
            {
                var projectBytes = await File.ReadAllBytesAsync(Path.Combine(projectPath, "content", "opaque", section.Blob));
                var stagedBytes = await File.ReadAllBytesAsync(Path.Combine(staging.RootPath, stagedOpaque.RelativePath, section.Blob));
                Equal(true, projectBytes.SequenceEqual(stagedBytes), $"opaque round trip {section.Name}");
            }
            var expectedBaseHashes = new Dictionary<BakeLayerId, string>
            {
                [BakeLayerId.World] = "5f70bf18a086007016e948b04aed3b82103a36bea41755b6cddfaf10ace3c6ef",
                [BakeLayerId.Sky] = "66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925",
                [BakeLayerId.Tfrags] = "f315f3f6d33215f8777a7d5a4b809f433729d13a86fe6adf3da5c11137e18273",
                [BakeLayerId.Collision] = "c2f480d4dda9f4522b9f6d590011636d904accfe59f12f9d66a0221c2558e3a2",
                [BakeLayerId.Lighting] = "2f1ca708f889230a7103c4c80f42c014a236702015618039ed298af3a7f26f17",
            };
            foreach (var layer in UyaBaseLayerSchema.Layers)
            {
                var snapshot = await UyaBaseLayerStore.StageAsync(
                    projectPath,
                    catalog,
                    staging,
                    bakePlan.Layers.Single(value => value.Layer == layer));
                var asset = baseInspection.Manifest.Layers.Single(value => value.Layer == layer).Assets.Single();
                var bytes = await File.ReadAllBytesAsync(Path.Combine(staging.RootPath, snapshot.RelativePath, asset.Name));
                var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                Equal(expectedBaseHashes[layer], checksum, $"{layer} golden output");
            }
            var maintenance = await AssetCatalogMaintenance.PreviewAsync(
                catalog.RootPath,
                [projectPath],
                readAdditionalReferences: UyaBaseLayerStore.ReadAssetIdsAsync);
            var baseAssetIds = baseInspection.Manifest.Layers.SelectMany(layer => layer.Assets)
                .Select(asset => asset.Asset.Id).ToHashSet();
            Equal(false, maintenance.Candidates.Any(candidate => baseAssetIds.Contains(candidate.Id)),
                "catalog maintenance protects base layers");

            var skyAsset = baseInspection.Manifest.Layers.Single(value => value.Layer == BakeLayerId.Sky).Assets.Single();
            File.Delete(catalog.ResolveBlobPath(skyAsset.Asset.Id)!);
            var missingBaseInputs = await UyaBaseLayerStore.CreateBakeInputsAsync(projectPath, catalog);
            var missingBasePlan = BakeLayerGraph.CreatePlan(
                bakeContext,
                bakeInputs.Select(input => missingBaseInputs.SingleOrDefault(value => value.Id == input.Id) ?? input).ToArray(),
                staging.Manifest);
            Equal(BakeLayerState.Blocked,
                missingBasePlan.Layers.Single(value => value.Layer == BakeLayerId.Sky).State,
                "missing base layer blocks only its output");
            Equal(BakeLayerState.Clean,
                missingBasePlan.Layers.Single(value => value.Layer == BakeLayerId.Tfrags).State,
                "unrelated base layer remains clean");
            await UyaProjectService.MigrateValidatedAsync(projectPath, catalog, new MemoryStream(iso, writable: false));
            Equal(true, (await UyaBaseLayerStore.InspectAsync(projectPath, catalog)).IsValid,
                "base layer repair restores catalog data");

            var project = await ForgeProjectWorkspace.OpenAsync(projectPath);
            var entity = project.Content.Entities.Single(candidate => candidate.Asset?.Kind == AssetKind.Moby);
            var modelLess = project.Content.Entities.Single(candidate => candidate.Asset is null);
            var tie = project.Content.Entities.Single(candidate => candidate.Asset?.Kind == AssetKind.Tie);
            var shrub = project.Content.Entities.Single(candidate => candidate.Asset?.Kind == AssetKind.Shrub);
            Equal(global.Id, entity.Asset!.Id, "global asset reference");
            Equal(1, modelLess.Provenance!.SourceIndex, "model-less source index");
            Equal(3, entity.Provenance!.Level, "entity source level");
            Equal(0, entity.Provenance.SourceIndex, "entity source index");
            Equal(tieAsset.Id, tie.Asset!.Id, "tie global asset reference");
            Equal(shrubAsset.Id, shrub.Asset!.Id, "shrub global asset reference");
            Equal("ties", tie.Layer, "tie layer");
            Equal("shrubs", shrub.Layer, "shrub layer");
            Equal(200, tie.Source!.ClassId, "tie source class");
            Equal(UyaTieInstancesReader.RecordSize, tie.Source.RawRecord.Length, "tie raw record");
            Equal(new ProjectVector3(40, 50, 60), tie.Transform.Position, "tie position");
            Equal(new ProjectVector3(2, 3, 4), shrub.Transform.Scale, "shrub scale");
            Equal(10f, entity.Transform.Position.X, "entity position");
            Near(0.0342708f, entity.Transform.Rotation.X, "ZYX rotation X");
            Near(0.10602051f, entity.Transform.Rotation.Y, "ZYX rotation Y");
            Near(0.14357218f, entity.Transform.Rotation.Z, "ZYX rotation Z");
            Near(0.9833474f, entity.Transform.Rotation.W, "ZYX rotation W");
            var stableId = entity.EntityId;
            project = await ForgeProjectWorkspace.OpenAsync(projectPath);
            Equal(stableId, project.Content.Entities.Single(candidate => candidate.Asset?.Kind == AssetKind.Moby).EntityId,
                "base entity ID survives reopen");
            project.Rename("Renamed project");
            project.RemoveEntity(stableId);
            await project.SaveAsync();
            var reopened = await ForgeProjectWorkspace.OpenAsync(projectPath);
            Equal("Renamed project", reopened.Manifest.Name, "project rename");
            Equal(3, reopened.Content.Entities.Count, "project-only entity deletion");
            Equal(true, File.Exists(catalog.ResolveBlobPath(global.Id)), "global asset survives deletion");
            var afterEditInput = await OpaqueContentStore.CreateBakeInputAsync(projectPath);
            var afterEditPlan = BakeLayerGraph.CreatePlan(
                bakeContext,
                bakeInputs.Select(input => input.Id == BakeLayerId.Opaque ? afterEditInput : input).ToArray(),
                staging.Manifest);
            Equal(BakeLayerState.Clean,
                afterEditPlan.Layers.Single(layer => layer.Layer == BakeLayerId.Opaque).State,
                "supported edits do not invalidate opaque content");
            Equal(true, UyaBaseLayerSchema.Layers.All(layer =>
                    afterEditPlan.Layers.Single(value => value.Layer == layer).State == BakeLayerState.Clean),
                "entity edits do not invalidate base geometry layers");

            var codePath = Path.Combine(projectPath, "content", "opaque", code.Blob);
            await File.WriteAllBytesAsync(codePath, [0, 0, 0, 0]);
            var altered = await OpaqueContentStore.CreateBakeInputAsync(projectPath);
            var alteredPlan = BakeLayerGraph.CreatePlan(
                bakeContext,
                bakeInputs.Select(input => input.Id == BakeLayerId.Opaque ? altered : input).ToArray(),
                staging.Manifest);
            Equal(BakeLayerState.Blocked,
                alteredPlan.Layers.Single(layer => layer.Layer == BakeLayerId.Opaque).State,
                "altered opaque content blocks bake");
            Equal(true, altered.Blockers!.Any(value => value.Contains("checksum changed", StringComparison.Ordinal)),
                "altered opaque content is actionable");
            await UyaProjectService.MigrateValidatedAsync(projectPath, catalog, new MemoryStream(iso, writable: false));
            File.Delete(codePath);
            var missingOpaque = await OpaqueContentStore.CreateBakeInputAsync(projectPath);
            Equal(true, missingOpaque.Blockers!.Any(value => value.Contains("is missing", StringComparison.Ordinal)),
                "missing opaque content is actionable");
            var repaired = await UyaProjectService.MigrateValidatedAsync(
                projectPath, catalog, new MemoryStream(iso, writable: false));
            Equal(false, repaired.MigrationPending, "opaque content repair completes");
            Equal(true, (await OpaqueContentStore.InspectAsync(projectPath)).IsValid, "opaque content repair restores bytes");

            var legacyPath = Path.Combine(root, "legacy-base-project");
            await UyaProjectService.CreateValidatedAsync(
                new MemoryStream(iso, writable: false), catalog,
                request with { ProjectPath = legacyPath, Name = "Legacy base" });
            var legacy = await ForgeProjectWorkspace.OpenAsync(legacyPath);
            var legacyMobyId = legacy.Content.Entities.First(value => value.Layer == "mobys").EntityId;
            foreach (var staticEntity in legacy.Content.Entities.Where(value => value.Layer is "ties" or "shrubs").ToArray())
                legacy.RemoveEntity(staticEntity.EntityId);
            await legacy.SaveAsync();
            var legacyManifestPath = Path.Combine(legacyPath, ForgeProjectWorkspace.ManifestFileName);
            var legacyManifest = JsonNode.Parse(await File.ReadAllBytesAsync(legacyManifestPath))!.AsObject();
            legacyManifest["baseLevel"]!["entityVersion"] = 0;
            await File.WriteAllTextAsync(legacyManifestPath,
                legacyManifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            var legacyContentPath = Path.Combine(legacyPath, ForgeProjectWorkspace.DefaultContentPath);
            var legacyContent = JsonNode.Parse(await File.ReadAllBytesAsync(legacyContentPath))!.AsObject();
            foreach (var legacyEntity in legacyContent["entities"]!.AsArray().Select(value => value!.AsObject()))
                if (legacyEntity["layer"]!.GetValue<string>() == "mobys") legacyEntity.Remove("source");
            await File.WriteAllTextAsync(legacyContentPath,
                legacyContent.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            var migrated = await UyaProjectService.MigrateValidatedAsync(
                legacyPath, catalog, new MemoryStream(iso, writable: false));
            Equal(false, migrated.MigrationPending, "base entity migration completes");
            legacy = await ForgeProjectWorkspace.OpenAsync(legacyPath);
            Equal(1, legacy.Content.Entities.Count(value => value.Layer == "ties"), "migration adds ties once");
            Equal(1, legacy.Content.Entities.Count(value => value.Layer == "shrubs"), "migration adds shrubs once");
            Equal(legacyMobyId, legacy.Content.Entities.First(value => value.Layer == "mobys").EntityId,
                "migration preserves existing moby identity");
            Equal(true, legacy.Content.Entities.Where(value => value.Layer == "mobys").All(value => value.Source is not null),
                "migration enriches existing moby source records");
            var migratedTie = legacy.Content.Entities.Single(value => value.Layer == "ties");
            legacy.RemoveEntity(migratedTie.EntityId);
            await legacy.SaveAsync();
            await UyaProjectService.MigrateValidatedAsync(
                legacyPath, catalog, new MemoryStream(iso, writable: false));
            legacy = await ForgeProjectWorkspace.OpenAsync(legacyPath);
            Equal(0, legacy.Content.Entities.Count(value => value.Layer == "ties"), "migration does not resurrect deleted ties");

            var refusedPath = Path.Combine(root, "refused");
            var missingCatalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "missing-catalog"));
            await ThrowsAsync<InvalidDataException>(() => UyaProjectService.CreateValidatedAsync(
                new MemoryStream(iso, writable: false), missingCatalog,
                request with { ProjectPath = refusedPath, AllowPartial = false }));
            Equal(false, Directory.Exists(refusedPath), "warning confirmation precedes project write");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static byte[] CreateIso()
    {
        const int headerSector = 0x500;
        var bytes = new byte[(headerSector + 3) * UyaLevelConstants.SectorSize];
        var info = UyaLevelConstants.RetailLevelInfoTableOffset + 3 * UyaLevelConstants.LevelInfoSize;
        WriteInt32(bytes, info + 0x08, headerSector);
        WriteInt32(bytes, info + 0x0c, 3);

        var wad = bytes.AsSpan(headerSector * UyaLevelConstants.SectorSize);
        WriteInt32(wad, 0x00, UyaLevelConstants.LevelWadHeaderSize);
        WriteInt32(wad, 0x04, headerSector);
        WriteInt32(wad, 0x10, 1);
        WriteInt32(wad, 0x14, 1);
        WriteInt32(wad, 0x20, 2);
        WriteInt32(wad, 0x24, 1);

        var levelData = wad[UyaLevelConstants.SectorSize..];
        WriteByteBlock(levelData, 0x00, 0x80, 4);
        WriteByteBlock(levelData, 0x08, 0x100, 0x160);
        WriteByteBlock(levelData, 0x10, 0x260, 1);
        WriteByteBlock(levelData, 0x48, 0x300, 0xc0);
        new byte[] { 0xde, 0xad, 0xbe, 0xef }.CopyTo(levelData[0x80..]);
        var assetHeader = levelData[0x100..];
        WriteInt32(assetHeader, 0x08, 0x40);
        WriteInt32(assetHeader, 0x10, 0x80);
        WriteInt32(assetHeader, 0x14, 0xa0);
        WriteInt32(assetHeader, 0x18, 1);
        WriteInt32(assetHeader, 0x1c, 0xc0);
        WriteInt32(assetHeader, 0x20, 1);
        WriteInt32(assetHeader, 0x24, 0xe0);
        WriteInt32(assetHeader, 0x28, 1);
        WriteInt32(assetHeader, 0x2c, 0x100);
        WriteInt32(assetHeader, 0xc0, 0x10);
        WriteInt32(assetHeader, 0xc4, 100);
        assetHeader.Slice(0xd0, 0x10).Fill(byte.MaxValue);
        WriteInt32(assetHeader, 0xe0, 0x20);
        WriteInt32(assetHeader, 0xe4, 200);
        assetHeader.Slice(0xf0, 0x10).Fill(byte.MaxValue);
        WriteInt32(assetHeader, 0x100, 0x30);
        WriteInt32(assetHeader, 0x104, 300);
        assetHeader.Slice(0x110, 0x10).Fill(byte.MaxValue);

        var assets = levelData[0x300..0x3c0];
        WriteInt32(assets, 0x40, 0x40);
        assets[0xa0..0xc0].Fill(0xcc);

        var gameplay = wad[(2 * UyaLevelConstants.SectorSize)..];
        WriteInt32(gameplay, 0x00, 0x400);
        WriteInt32(gameplay, 0x04, 0x2ac);
        WriteInt32(gameplay, 0x34, 0x9c);
        WriteInt32(gameplay, 0x40, 0x10c);
        WriteInt32(gameplay, 0x4c, 0x18c);
        var ties = gameplay[0x9c..];
        WriteInt32(ties, 0, 1);
        var tie = ties[0x10..];
        WriteInt32(tie, 0, 200);
        WriteSingle(tie, 0x10, 1);
        WriteSingle(tie, 0x24, 1);
        WriteSingle(tie, 0x38, 1);
        WriteSingle(tie, 0x40, 40);
        WriteSingle(tie, 0x44, 50);
        WriteSingle(tie, 0x48, 60);
        var shrubs = gameplay[0x10c..];
        WriteInt32(shrubs, 0, 1);
        var shrub = shrubs[0x10..];
        WriteInt32(shrub, 0, 300);
        WriteSingle(shrub, 0x10, 2);
        WriteSingle(shrub, 0x24, 3);
        WriteSingle(shrub, 0x38, 4);
        WriteSingle(shrub, 0x40, 70);
        WriteSingle(shrub, 0x44, 80);
        WriteSingle(shrub, 0x48, 90);
        var mobys = gameplay[0x18c..];
        WriteInt32(mobys, 0x00, 2);
        var instance = mobys[0x10..];
        WriteInt32(instance, 0x00, 0x88);
        WriteInt32(instance, 0x10, 7);
        WriteInt32(instance, 0x28, 100);
        WriteSingle(instance, 0x2c, 2);
        WriteSingle(instance, 0x40, 10);
        WriteSingle(instance, 0x44, 20);
        WriteSingle(instance, 0x48, 30);
        WriteSingle(instance, 0x4c, 0.1f);
        WriteSingle(instance, 0x50, 0.2f);
        WriteSingle(instance, 0x54, 0.3f);
        WriteInt32(instance, 0x68, -1);
        var missingInstance = instance[0x88..];
        WriteInt32(missingInstance, 0x00, 0x88);
        WriteInt32(missingInstance, 0x10, 8);
        WriteInt32(missingInstance, 0x28, 101);
        WriteSingle(missingInstance, 0x2c, 1);
        WriteInt32(missingInstance, 0x68, -1);
        WriteInt32(gameplay, 0x2ac, 1);
        return bytes;
    }

    private static void WriteInt32(Span<byte> bytes, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], value);

    private static void WriteSingle(Span<byte> bytes, int offset, float value) =>
        WriteInt32(bytes, offset, BitConverter.SingleToInt32Bits(value));

    private static void WriteByteBlock(Span<byte> bytes, int offset, int blockOffset, int length)
    {
        WriteInt32(bytes, offset, blockOffset);
        WriteInt32(bytes, offset + 4, length);
    }

    private static byte[] CanonicalAsset(AssetKind kind, byte modelByte)
    {
        var definitionLength = kind == AssetKind.Shrub ? 0x30 : 0x20;
        var bytes = new byte[19 + definitionLength];
        "HFUYA1"u8.CopyTo(bytes);
        WriteInt32(bytes, 6, definitionLength);
        WriteInt32(bytes, 10 + definitionLength, 1);
        bytes[14 + definitionLength] = modelByte;
        WriteInt32(bytes, 15 + definitionLength, 0);
        return bytes;
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    private static void Near(float expected, float actual, string context)
    {
        if (MathF.Abs(expected - actual) > 0.000001f)
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}

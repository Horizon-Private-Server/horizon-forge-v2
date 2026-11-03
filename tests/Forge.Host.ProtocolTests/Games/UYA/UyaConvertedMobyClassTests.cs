using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Moby;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaConvertedMobyClassTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-moby-class-{Guid.NewGuid():N}");
        try
        {
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            const int sourceClass = 0x24f9;
            var model = MobyModelPacker.Build(new() { Scale = 1 });
            var entry = await catalog.PutAsync(AssetKind.Moby, 1,
                CatalogModelCodec.Encode(GameId.DL, new byte[0x20], model, []),
                new("test", new("DL", "NTSC-U", "1.00", "level01", "classes.bin", 0, new string('a', 32)),
                    [$"moby:0x{sourceClass:X4}"], ["vanilla"]));
            var workspace = await ForgeProjectWorkspace.CreateAsync(Path.Combine(root, "project"), "Moby class",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)), []);
            var entity = await UyaAssetPlacementService.CreateAsync(workspace, catalog.RootPath,
                new(entry.Id, AssetKind.Moby, sourceClass, ProjectTransform.Identity), default);
            workspace.AddEntity(entity with { Name = "My DL prop" });
            Check(entity.Source!.ClassId == sourceClass && UyaMobyInstancesReader.ReadInstance(entity.Source.RawRecord).ClassId == sourceClass,
                "new DL placement preserves the original class in metadata and native records");
            UyaCustomTieClassRegistry.Validate(workspace.Content.CustomClasses);
            var modelLessTarget = await ForgeProjectWorkspace.CreateAsync(Path.Combine(root, "collision"), "Collision",
                workspace.Manifest.Target, workspace.Manifest.BaseLevel,
                [entity with { Asset = null, Source = entity.Source with { ModelLess = true } }]);
            var targetFingerprint = modelLessTarget.CurrentFingerprint;
            try
            {
                await UyaAssetPlacementService.CreateAsync(modelLessTarget, catalog.RootPath,
                    new(entry.Id, AssetKind.Moby, sourceClass, ProjectTransform.Identity), default);
                throw new Exception("Expected model-less class conflict");
            }
            catch (InvalidDataException) { }
            Check(modelLessTarget.CurrentFingerprint == targetFingerprint, "placement preserves existing model-less native classes");
            try
            {
                UyaCustomTieClassRegistry.AllocateConverted(ProjectCustomClassRegistry.Empty,
                    AssetKind.Moby, entry.Id, sourceClass, new HashSet<int> { sourceClass });
                throw new Exception("Expected class conflict");
            }
            catch (InvalidDataException) { }
            var before = workspace.Content;
            var legacyEntity = before.Entities.Single();
            var oldRecord = GameplayInstanceConverter.RebaseMobyClass(GameId.UYA, legacyEntity.Source!.RawRecord, sourceClass, 0x9000);
            Check(UyaMobyInstancesReader.ReadInstance(oldRecord).ClassId == 0x9000, "SDK class rebasing writes the target identity");
            for (var index = 0; index < oldRecord.Length; index++)
                if (index is < 0x28 or >= 0x2c) Check(oldRecord[index] == legacyEntity.Source.RawRecord[index], "SDK rebasing retains every other record byte");
            try
            {
                GameplayInstanceConverter.RebaseMobyClass(GameId.UYA, oldRecord, sourceClass, 0x9001);
                throw new Exception("Expected stale class rejection");
            }
            catch (InvalidDataException) { }
            await workspace.CommitAssetsAsync(before with
            {
                Entities = [legacyEntity with { Source = legacyEntity.Source with { ClassId = 0x9000, RawRecord = oldRecord } }],
                CustomClasses = before.CustomClasses with { Allocations = before.CustomClasses.Allocations
                    .Select(value => value with { ClassId = 0x9000 }).ToArray() },
                Sounds = before.Sounds with { MobyRemaps = [new(0, 0x9000, entity.Asset, 0, new(0, 0), [])] },
            }, []);
            var legacy = workspace.CurrentFingerprint;
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try
            {
                await UyaConvertedMobyClassRepairService.HydrateAsync(workspace, cancellation.Token);
                throw new Exception("Expected cancellation");
            }
            catch (OperationCanceledException) { }
            Check(workspace.CurrentFingerprint == legacy, "cancelled migration preserves the project");
            var collision = entity with { EntityId = EntityId.New(), Asset = null };
            workspace.AddEntity(collision);
            var conflictFingerprint = workspace.CurrentFingerprint;
            Check((await UyaConvertedMobyClassRepairService.HydrateAsync(workspace, default))?.Severity == EditorDiagnosticSeverity.Warning
                && workspace.CurrentFingerprint == conflictFingerprint, "class conflict preserves every existing entity");
            workspace.RemoveEntities([collision.EntityId]);
            Check(await UyaConvertedMobyClassRepairService.HydrateAsync(workspace, default) is not null, "legacy classes are repaired");
            var restoredEntity = workspace.Content.Entities.Single();
            Check(restoredEntity with { Source = legacyEntity.Source } == legacyEntity
                && restoredEntity.Source!.RawRecord.SequenceEqual(legacyEntity.Source.RawRecord)
                && workspace.Content.Sounds.MobyRemaps.Single().ClassId == sourceClass,
                "repair restores original records, stable identity, custom name and matching sound class");
            Check(workspace.Content.Assets.SequenceEqual(before.Assets) && workspace.Content.AssetOverrides.SequenceEqual(before.AssetOverrides),
                "class repair retains all converted model/texture edits");
            await workspace.SaveAsync();
            workspace = await ForgeProjectWorkspace.OpenAsync(workspace.RootPath);
            Check(await UyaConvertedMobyClassRepairService.HydrateAsync(workspace, default) is null, "restored classes persist and repair is idempotent");
            await VerifyVariantReplacementAsync(root, catalog, entry, sourceClass);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static async Task VerifyVariantReplacementAsync(string root, AssetCatalogStore catalog,
        AssetCatalogEntry original, int classId)
    {
        var alternate = await catalog.PutAsync(AssetKind.Moby, 1,
            CatalogModelCodec.Encode(GameId.DL, new byte[0x20], MobyModelPacker.Build(new() { Scale = 2 }), []),
            new("test", new("DL", "NTSC-U", "1.00", "level02", "classes.bin", 0, new string('a', 32)),
                [$"moby:0x{classId:X4}"], ["vanilla"]));
        var workspace = await ForgeProjectWorkspace.CreateAsync(Path.Combine(root, "variants"), "Variants",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)), []);
        var entity = await UyaAssetPlacementService.CreateAsync(workspace, catalog.RootPath,
            new(original.Id, AssetKind.Moby, classId, ProjectTransform.Identity), default);
        workspace.AddEntity(entity);
        var map = new ProjectMobySoundMap(0, classId, entity.Asset, 0, new(0, 0), []);
        await workspace.SetSoundsAsync(workspace.Content.Sounds with { MobyRemaps = [map] });
        await workspace.SaveAsync();
        await using var runtime = new EditorRuntime(placementResolver: UyaAssetPlacementService.CreateAsync);
        await runtime.OpenAsync(workspace.RootPath, catalog.RootPath, TimeSpan.Zero);
        var before = await runtime.ReadWorkspaceAsync(workspace.RootPath,
            (value, _) => Task.FromResult(value.CurrentFingerprint), default);
        var place = new EditorCommand(Guid.NewGuid().ToString("D"), EditorCommandKind.CreateEntityFromAsset, [],
            Placement: new(alternate.Id, AssetKind.Moby, classId, ProjectTransform.Identity));
        try
        {
            await runtime.ExecuteAsync(place);
            throw new Exception("Expected active variant conflict");
        }
        catch (InvalidDataException) { }
        Check(await runtime.ReadWorkspaceAsync(workspace.RootPath,
            (value, _) => Task.FromResult(value.CurrentFingerprint == before), default),
            "an active variant conflict preserves all project content");
        await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.DeleteEntities, [entity.EntityId]));
        var deleted = await runtime.ReadWorkspaceAsync(workspace.RootPath,
            (value, _) => Task.FromResult(value.CurrentFingerprint), default);
        await runtime.ExecuteAsync(place with { Id = Guid.NewGuid().ToString("D") });
        var placed = (await runtime.GetSnapshotAsync()).Entities.Single();
        Check(placed.Asset?.Id == alternate.Id && placed.SourceClassId == classId,
            "an alternate variant reclaims the deleted variant's original class ID");
        Check(await runtime.ReadWorkspaceAsync(workspace.RootPath, (value, _) => Task.FromResult(
            value.Content.CustomClasses.Allocations.Single().Asset.Id == alternate.Id
            && value.Content.Sounds.MobyRemaps.Count == 0), default), "replacement retires the unused variant's allocation and remap");
        await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
        Check(await runtime.ReadWorkspaceAsync(workspace.RootPath, (value, _) => Task.FromResult(
            value.CurrentFingerprint == deleted), default), "undo restores the deleted variant's allocation and sound map");
        await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
        Check((await runtime.GetSnapshotAsync()).Entities.Single().Asset?.Id == original.Id,
            "undoing deletion restores the original variant");
        await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
        await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
        await runtime.SaveAsync();
        workspace = await ForgeProjectWorkspace.OpenAsync(workspace.RootPath);
        Check(workspace.Content.Entities.Single().Asset?.Id == alternate.Id
            && workspace.Content.CustomClasses.Allocations.Single().ClassId == classId,
            "alternate variant placement survives redo and save/reopen");
    }

    public static async Task QualifyAsync(string sourceProject, string scratch, string catalogRoot)
    {
        if (Directory.Exists(scratch)) throw new InvalidOperationException("A new scratch project is required.");
        Directory.CreateDirectory(scratch);
        File.Copy(Path.Combine(sourceProject, "forge-project.json"), Path.Combine(scratch, "forge-project.json"));
        foreach (var directory in new[] { "assets", "content" })
        foreach (var path in Directory.EnumerateFiles(Path.Combine(sourceProject, directory), "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(scratch, Path.GetRelativePath(sourceProject, path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(path, destination);
        }
        var workspace = await ForgeProjectWorkspace.OpenAsync(scratch);
        var original = workspace.Content;
        Check(await UyaConvertedMobyClassRepairService.HydrateAsync(workspace, default) is not null, "retail project has old DL classes");
        foreach (var allocation in original.CustomClasses.Allocations.Where(value => value.Asset.Kind == AssetKind.Moby
            && value.Provenance.Origin == ProjectCustomClassOrigin.ConvertedAsset))
        {
            var restored = workspace.Content.CustomClasses.Allocations.Single(value => value.Asset == allocation.Asset);
            Check(restored.ClassId == allocation.Provenance.SourceClassId, "retail source class is restored");
            foreach (var entity in original.Entities.Where(value => value.Asset == allocation.Asset))
            {
                var actual = workspace.Content.Entities.Single(value => value.EntityId == entity.EntityId);
                Check(actual.Transform == entity.Transform && actual.Asset == entity.Asset, "retail entity transforms and asset identity retained");
                Check(actual.Source!.RawRecord.SequenceEqual(GameplayInstanceConverter.RebaseMobyClass(GameId.UYA,
                    entity.Source!.RawRecord, allocation.ClassId, restored.ClassId)), "retail native records only change class bytes");
            }
        }
        Check(workspace.Content.Assets.SequenceEqual(original.Assets) && workspace.Content.AssetOverrides.SequenceEqual(original.AssetOverrides),
            "retail converted models remain byte exact");
        Check(workspace.Content.Sounds.Programs.SequenceEqual(original.Sounds.Programs)
            && workspace.Content.Sounds.Samples.SequenceEqual(original.Sounds.Samples)
            && workspace.Content.Sounds.GlobalRemaps.SequenceEqual(original.Sounds.GlobalRemaps),
            "retail sound programs, samples and global remaps remain unchanged");
        foreach (var map in original.Sounds.MobyRemaps)
        {
            var allocation = original.CustomClasses.Allocations.SingleOrDefault(value => value.Asset.Kind == AssetKind.Moby
                && value.ClassId == map.ClassId && value.Provenance.Origin == ProjectCustomClassOrigin.ConvertedAsset);
            var actual = workspace.Content.Sounds.MobyRemaps.Single(value => value.DefinitionIndex == map.DefinitionIndex);
            Check(actual == map with { ClassId = allocation?.Provenance.SourceClassId ?? map.ClassId },
                "retail sound slots preserve their original programs while their class identity is repaired");
        }
        var catalog = await AssetCatalogStore.OpenReadOnlyAsync(catalogRoot);
        var inputs = await UyaStaticLayerStore.CreateBakeInputsAsync(workspace, catalog, default);
        Check(inputs.Single(value => value.Id == BakeLayerId.Mobys).Blockers?.Count is null or 0, "restored retail mobys pass static bake preparation");
        var plan = BakeLayerGraph.CreatePlan(new(workspace.Manifest.Target, "qualification", "qualification"), inputs);
        var staging = await BakeStagingStore.OpenAsync(scratch);
        var snapshot = await UyaStaticLayerStore.StageAsync(workspace, catalog, staging,
            plan.Layers.Single(value => value.Layer == BakeLayerId.Mobys), default);
        var stagedRoot = ForgeProjectPersistence.ResolveRelativePath(staging.RootPath, snapshot.RelativePath);
        var native = UyaMobyInstancesReader.Read(await File.ReadAllBytesAsync(Path.Combine(stagedRoot, "instances.bin")));
        foreach (var allocation in workspace.Content.CustomClasses.Allocations.Where(value => value.Asset.Kind == AssetKind.Moby
            && value.Provenance.Origin == ProjectCustomClassOrigin.ConvertedAsset))
            Check(native.Instances.Count(value => value.ClassId == allocation.ClassId)
                == workspace.Content.Entities.Count(value => value.Asset == allocation.Asset), "native bake retains the restored DL instance classes");
        await workspace.SaveAsync();
        workspace = await ForgeProjectWorkspace.OpenAsync(scratch);
        Check(await UyaConvertedMobyClassRepairService.HydrateAsync(workspace, default) is null, "retail repair survives reopening");
        Console.WriteLine("Retail DL Moby oClasses restored in a scratch project; transforms, model/texture edits and non-class instance bytes retained; native Moby bake and save/reopen passed.");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}

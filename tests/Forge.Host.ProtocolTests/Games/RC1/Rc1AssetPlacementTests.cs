using Forge.Host.Domain;
using Forge.Host.Games.RC1;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Ties;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.RC1;

internal static class Rc1AssetPlacementTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-rc1-placement-{Guid.NewGuid():N}");
        try
        {
            var catalogPath = Path.Combine(root, "catalog");
            await Rc1AssetImportService.ImportValidatedAsync(new MemoryStream(Rc1AssetImportTests.CreateIso(transparent: true)),
                new(GameId.RC1, "synthetic.iso", catalogPath, new string('a', 32), "1.00", "rc1-test"));
            var catalog = await AssetCatalogStore.OpenAsync(catalogPath);
            var entries = catalog.Query(new()).Where(entry => entry.Kind is AssetKind.Tie or AssetKind.Shrub or AssetKind.Moby).ToArray();
            var project = Path.Combine(root, "project");
            await ForgeProjectWorkspace.CreateAsync(project, "RC1 assets", new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 3, new string('b', 32)), []);
            await UyaStaticLayerStore.WriteSourceAsync(project, new("UYA", "NTSC-U", "1.00", 3, new string('b', 32)),
            [
                new(BakeLayerId.Ties, UyaTieInstancesReader.RecordSize, [0, 0, 0], []),
                new(BakeLayerId.Shrubs, UyaShrubInstancesReader.RecordSize, [0, 0, 0], []),
                new(BakeLayerId.Mobys, UyaMobyInstancesReader.RecordSize, [0, 0, 0], []),
            ]);
            await using (var runtime = new EditorRuntime(placementResolver: UyaAssetPlacementService.CreateAsync))
            {
                await runtime.OpenAsync(project, catalogPath, TimeSpan.Zero);
                foreach (var entry in entries)
                {
                    Check(UyaAssetPlacementService.PlacementCompatibility(entry, "NTSC-U", "1.00", [0x1234], true).CanPlace, "RC1 assets are placeable");
                    Check(!UyaAssetPlacementService.PlacementCompatibility(entry with { Sources = [entry.Sources[0] with { Revision = "1.01" }] },
                        "NTSC-U", "1.00", [0x1234], true).CanPlace, "RC1 rejects unsupported source revisions");
                    var placed = await runtime.ExecuteAsync(Place(entry));
                    Check(placed.Entities.Single(entity => entity.EntityId == placed.Selection.Single()).SourceClassId == 0x1234,
                        "RC1 placement preserves the source class ID");
                    await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
                    Check(await runtime.ReadWorkspaceAsync(project, (workspace, _) => Task.FromResult(
                        !workspace.Content.CustomClasses.Allocations.Any(allocation => allocation.Asset.Id == entry.Id)), default), "undo restores class allocation");
                    await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
                }
                var tie = entries.Single(entry => entry.Kind == AssetKind.Tie);
                var model = CatalogModelCodec.Decode(await File.ReadAllBytesAsync(catalog.ResolveBlobPath(tie.Id)!));
                model.ModelBytes[0x6c] ^= 1;
                var variant = await catalog.PutAsync(AssetKind.Tie, 1,
                    CatalogModelCodec.Encode(GameId.RC1, model.DefinitionBytes, model.ModelBytes, model.Textures),
                    new("rc1-test", tie.Sources[0], tie.Aliases, tie.Tags));
                var before = await runtime.ReadWorkspaceAsync(project, (workspace, _) => Task.FromResult(workspace.CurrentFingerprint), default);
                try { await runtime.ExecuteAsync(Place(variant)); throw new Exception("Expected class conflict"); }
                catch (InvalidDataException) { }
                Check(await runtime.ReadWorkspaceAsync(project, (workspace, _) => Task.FromResult(workspace.CurrentFingerprint), default) == before,
                    "a conflicting variant leaves the project untouched");
                var entities = await runtime.ReadWorkspaceAsync(project, (workspace, _) => Task.FromResult(
                    workspace.Content.Entities.Where(entity => entity.Asset?.Id == tie.Id).Select(entity => entity.EntityId).ToArray()), default);
                await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.DeleteEntities, entities));
                await runtime.ExecuteAsync(Place(variant));
                await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
                await runtime.ExecuteAsync(new(Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
                await runtime.SaveAsync();
            }
            var workspace = await ForgeProjectWorkspace.OpenAsync(project);
            Check(workspace.Content.Entities.Count == 3 && workspace.Content.CustomClasses.Allocations.All(allocation => allocation.ClassId == 0x1234),
                "RC1 placements survive save/reopen");
            foreach (var allocation in workspace.Content.CustomClasses.Allocations)
            {
                var resolved = workspace.ResolveAsset(allocation.Asset, catalog)!;
                Check(resolved.ProjectAttached && resolved.Effective.Id != allocation.Asset.Id, "native conversion is attached");
                Check(CatalogModelCodec.Decode(await File.ReadAllBytesAsync(resolved.Path)).Game == GameId.UYA, "conversion uses a native UYA envelope");
            }
            await VerifyCullingRepairAsync(workspace);
            var inputs = await UyaStaticLayerStore.CreateBakeInputsAsync(workspace, catalog, default);
            Check(inputs.All(input => input.Blockers?.Count is null or 0), "RC1 placements can bake");
            var plan = BakeLayerGraph.CreatePlan(new(workspace.Manifest.Target, "test", "test"), inputs);
            var staging = await BakeStagingStore.OpenAsync(project);
            foreach (var layer in UyaStaticLayerSchema.Layers)
                await UyaStaticLayerStore.StageAsync(workspace, catalog, staging, plan.Layers.Single(value => value.Layer == layer), default);
            Check((await UyaTextureInventoryService.BuildAsync(workspace, catalog, default)).Textures.Count == 1, "RC1 textures enter the bake inventory");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static async Task VerifyCullingRepairAsync(ForgeProjectWorkspace workspace)
    {
        var allocation = workspace.Content.CustomClasses.Allocations.Single(value => value.Asset.Kind == AssetKind.Tie);
        var binding = workspace.Content.AssetOverrides.Single(value => value.Source == allocation.Asset);
        var source = CatalogModelCodec.Decode(await workspace.ReadAttachedAssetVerifiedAsync(
            allocation.Asset, ForgeProjectWorkspace.MaxAttachedAssetBytes, default));
        var native = UyaCanonicalAssetCodec.Decode(await workspace.ReadAttachedAssetVerifiedAsync(
            binding.Replacement, ForgeProjectWorkspace.MaxAttachedAssetBytes, default));
        Check(TieClassReader.Read(native.ModelBytes).PacketTables.SelectMany(table => table.Packets).All(packet => packet.BfcDistance == 0),
            "new RC1 leaf placements render both sides");
        var legacy = LevelAssetModelConverter.Convert(GameId.RC1, GameId.UYA, FrontendAssetKind.Tie, source.ModelBytes, allocation.ClassId);
        Check(TieClassReader.Read(legacy).PacketTables.SelectMany(table => table.Packets).Any(packet => packet.BfcDistance != 0),
            "old placement fixture has the culling bug");
        async Task Replace(byte[] model)
        {
            var attachments = new ProjectAssetAttachmentCollector();
            var bytes = UyaCanonicalAssetCodec.Encode(native.DefinitionBytes, model, native.Textures);
            var id = AssetId.Compute(AssetKind.Tie, 1, bytes);
            var existing = workspace.Content.Assets.SingleOrDefault(value => value.Id == id);
            var replacement = attachments.Attach(AssetKind.Tie, 1, bytes, existing is null ? allocation.Asset.Id : existing.ParentId);
            await workspace.CommitAssetsAsync(workspace.Content with { AssetOverrides = workspace.Content.AssetOverrides.Select(value =>
                value.Source == allocation.Asset ? value with { Replacement = replacement } : value).ToArray() }, attachments.Attachments, default);
        }
        await Replace(legacy);
        var before = workspace.CurrentFingerprint;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await UyaConvertedStaticAssetRepairService.HydrateAsync(workspace, cancelled.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            Check(workspace.CurrentFingerprint == before, "cancelled RC1 culling repair preserves project state");
        }
        Check((await UyaConvertedStaticAssetRepairService.HydrateAsync(workspace, default))?.Severity == EditorDiagnosticSeverity.Info,
            "old RC1 leaf placements repair without a catalog reimport");
        Check(workspace.Content.AssetOverrides.Single(value => value.Source == allocation.Asset).Replacement == binding.Replacement,
            "repair restores exactly the current conversion, retaining textures and allocation");
        Check(await UyaConvertedStaticAssetRepairService.HydrateAsync(workspace, default) is null, "RC1 culling repair is idempotent");
        legacy[0x4c] ^= 1;
        await Replace(legacy);
        before = workspace.CurrentFingerprint;
        Check((await UyaConvertedStaticAssetRepairService.HydrateAsync(workspace, default))?.Severity == EditorDiagnosticSeverity.Warning
            && workspace.CurrentFingerprint == before, "independent model edits are preserved with a warning");
        await Replace(native.ModelBytes);
    }

    private static EditorCommand Place(AssetCatalogEntry entry) => new(Guid.NewGuid().ToString("D"), EditorCommandKind.CreateEntityFromAsset, [],
        Placement: new(entry.Id, entry.Kind, 0x1234, ProjectTransform.Identity, AssetSourceSelection.From(entry.Sources[0])));
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}

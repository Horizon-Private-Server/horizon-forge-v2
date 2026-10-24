using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Textures.Palettes;
using RatchetPs2.Core.Ties;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaCustomTieProjectTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-custom-tie-project-{Guid.NewGuid():N}");
        var projectPath = Path.Combine(root, "project");
        try
        {
            var workspace = await CreateAsync(projectPath);
            var firstRequest = Request(0, forceBackfaceCulling: true);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await ThrowsAsync<OperationCanceledException>(() =>
                    UyaCustomTieProjectService.ImportAsync(workspace, firstRequest, cancelled.Token));
            }
            Equal(0, workspace.Content.Assets.Count, "cancelled import commits no blobs");
            Equal(0, workspace.Content.CustomClasses.Allocations.Count, "cancelled import commits no class");
            await ThrowsAsync<InvalidDataException>(() => UyaCustomTieProjectService.ImportAsync(
                workspace, firstRequest with { SourceBytes = "not gltf"u8.ToArray() }));
            Equal(0, workspace.Content.Assets.Count, "failed import commits no asset metadata");

            await using (var runtime = new EditorRuntime(
                customAssetCommandExecutor: UyaCustomAssetProjectService.ExecuteAsync))
            {
                await runtime.OpenAsync(projectPath, TimeSpan.Zero);
                await runtime.ExecuteAsync(Command(EditorCommandKind.ImportCustomTie,
                    new(Import: firstRequest)));
                await runtime.ExecuteAsync(Command(EditorCommandKind.Undo));
                await runtime.SaveAsync();
                Equal(0, (await ForgeProjectWorkspace.OpenAsync(projectPath)).Content.CustomModels.Models.Count,
                    "undo removes imported model state");
                await runtime.ExecuteAsync(Command(EditorCommandKind.Redo));
                await runtime.SaveAsync();
            }

            workspace = await ForgeProjectWorkspace.OpenAsync(projectPath);
            var original = workspace.Content.CustomModels.Models.Single();
            var first = await UyaCustomTieProjectService.ImportAsync(
                workspace, firstRequest with { SourceName = "same-mesh-renamed.gltf" });
            Equal(original.Canonical, first.Canonical, "same geometry retains its canonical identity");
            Equal("same-mesh-renamed.gltf", first.SourceName,
                "re-importing the same geometry refreshes its derived model");
            Equal(1, workspace.Content.CustomModels.Models.Count,
                "refreshing the same geometry does not duplicate the custom model");
            Equal(UyaCustomTieClassRegistry.FirstClassId,
                workspace.Content.CustomClasses.Allocations.Single().ClassId,
                "import allocates the first project-native class");
            Equal(1, workspace.Content.Assets.Count(asset => asset.Kind == AssetKind.Texture),
                "converter textures are content addressed once");
            var nativeBytes = await workspace.ReadAttachedAssetVerifiedAsync(
                first.Native, ForgeProjectWorkspace.MaxAttachedAssetBytes);
            var native = UyaCustomTieNativeCodec.Read(nativeBytes);
            Equal(true, native.DefinitionBytes.Length > 0 && native.ModelBytes.Length > 0,
                "native definition and model are retained together");
            var nativeTie = TieClassReader.Read(native.ModelBytes);
            Equal(true, nativeTie.PacketTables[0].Packets.Count > 0
                    && nativeTie.PacketTables.Skip(1).All(table => table.Offset == 0 && table.Packets.Count == 0),
                "native conversion emits authored geometry only in LOD0");
            Equal(true, nativeTie.PacketTables[0].Packets.All(packet => packet.BfcDistance == 0x7f),
                "forced backface culling reaches every native packet");
            Equal(1, first.Metadata.Primitives.Count, "primitive mapping is retained for preview comparison");
            Equal(3, first.Metadata.VertexCount, "source vertex count is retained");
            Equal(1, first.Metadata.TriangleCount, "source triangle count is retained");
            Equal(1f, first.Metadata.Bounds.MaximumX, "source bounds are retained");

            var previewCache = Path.Combine(root, "preview-cache");
            var catalogPath = Path.Combine(root, "catalog");
            using (var previewCancellation = new CancellationTokenSource())
            {
                previewCancellation.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => UyaAssetPreviewService.PrepareAsync(new(
                    previewCache, catalogPath, first.Canonical.Id, AssetKind.Tie, "UYA", "custom-target",
                    ProjectPath: projectPath), "test-sdk", previewCancellation.Token));
            }
            var sourcePreview = await UyaAssetPreviewService.PrepareAsync(new(
                previewCache, catalogPath, first.Canonical.Id, AssetKind.Tie, "UYA", "custom-source",
                ProjectPath: projectPath), "test-sdk");
            var targetPreview = await UyaAssetPreviewService.PrepareAsync(new(
                previewCache, catalogPath, first.Canonical.Id, AssetKind.Tie, "UYA", "custom-target",
                ProjectPath: projectPath), "test-sdk");
            var thumbnailPreview = await UyaAssetPreviewService.PrepareAsync(new(
                previewCache, catalogPath, first.Canonical.Id, AssetKind.Tie, "UYA", "custom-thumbnail",
                ProjectPath: projectPath), "test-sdk");
            Equal(false, sourcePreview.CacheHit, "source preview is materialized once");
            Equal(false, targetPreview.CacheHit, "target preview is materialized once");
            Equal("preview-details.json", targetPreview.DetailsPath, "preview metadata route");
            Equal("diagnostic-target.glb", targetPreview.DiagnosticGlbPath, "diagnostic GLB route");
            Equal(null, thumbnailPreview.DetailsPath, "thumbnail skips preview metadata");
            Equal(null, thumbnailPreview.DiagnosticGlbPath, "thumbnail skips diagnostic GLB");
            Equal(true, File.Exists(Path.Combine(sourcePreview.RootPath, sourcePreview.ModelPath)),
                "source view reopens the retained glTF");
            Equal(true, File.Exists(Path.Combine(targetPreview.RootPath, targetPreview.ModelPath)),
                "target view reopens converted native TIE bytes");
            var details = JsonDocument.Parse(await File.ReadAllBytesAsync(
                Path.Combine(targetPreview.RootPath, targetPreview.DetailsPath!)));
            Equal(false, details.RootElement.GetProperty("authoritative").GetBoolean(),
                "preview metadata marks debug output non-authoritative");
            Equal(1, details.RootElement.GetProperty("primitives").GetArrayLength(),
                "preview metadata maps source primitives to target ranges");
            var debugGlb = await File.ReadAllBytesAsync(
                Path.Combine(targetPreview.RootPath, targetPreview.DiagnosticGlbPath!));
            Equal(0x46546C67u, BinaryPrimitives.ReadUInt32LittleEndian(debugGlb),
                "diagnostic export is a GLB container");
            using (var diagnosticJson = ReadGlbJson(debugGlb))
                Equal("Debug inspection artifact only; native project assets remain authoritative.",
                    diagnosticJson.RootElement.GetProperty("asset").GetProperty("extras")
                        .GetProperty("notice").GetString(),
                    "diagnostic export labels non-glTF target semantics");
            var cachedTarget = await UyaAssetPreviewService.PrepareAsync(new(
                previewCache, catalogPath, first.Canonical.Id, AssetKind.Tie, "UYA", "custom-target",
                ProjectPath: projectPath), "test-sdk");
            Equal(true, cachedTarget.CacheHit, "target preview cache is reused");
            var cachedDebugGlb = await File.ReadAllBytesAsync(
                Path.Combine(cachedTarget.RootPath, cachedTarget.DiagnosticGlbPath!));
            Equal(true, debugGlb.SequenceEqual(cachedDebugGlb),
                "diagnostic GLB export is deterministic");

            var textureA = new ProjectAssetReference(
                AssetId.Compute(AssetKind.Texture, ProjectTextureAssetSchema.CanonicalFormatVersion, "a"u8),
                AssetKind.Texture);
            var textureB = new ProjectAssetReference(
                AssetId.Compute(AssetKind.Texture, ProjectTextureAssetSchema.CanonicalFormatVersion, "b"u8),
                AssetKind.Texture);
            var materialOrder = UyaCustomTieTextureReader.OrderMaterialTextures(first with
            {
                Textures = [textureA, textureB],
                Materials =
                [
                    new(1, 0, textureA, false, false),
                    new(0, 1, textureB, false, false),
                ],
            });
            Equal(true, materialOrder.SequenceEqual([textureB, textureA]),
                "custom TIE textures follow native material slots instead of asset hash order");

            var uvWorkspace = await CreateAsync(Path.Combine(root, "uv-project"));
            var uvModel = await UyaCustomTieProjectService.ImportAsync(uvWorkspace, UvRequest());
            var uvPackage = await UyaCustomTiePreviewService.BuildTargetAsync(
                uvWorkspace, uvModel, CancellationToken.None);
            using (var uvDocument = JsonDocument.Parse(ReadPackageEntry(uvPackage, "model.gltf")))
            {
                var texCoords = ReadIndexedTexCoords(
                    uvDocument.RootElement,
                    ReadPackageEntry(uvPackage, "model.buffer.bin"));
                Equal(true, texCoords.Max(value => value.U) - texCoords.Min(value => value.U) > 0.9f,
                    "target preview preserves authored repeated-UV tile offsets");
            }
            var uvNative = UyaCustomTieNativeCodec.Read(await uvWorkspace.ReadAttachedAssetVerifiedAsync(
                uvModel.Native, ForgeProjectWorkspace.MaxAttachedAssetBytes));
            var uvTextures = await UyaCustomTieTextureReader.ReadMaterialTexturesAsync(
                uvWorkspace, uvModel, CancellationToken.None);
            var canonicalUv = UyaCanonicalAssetCodec.Encode(
                uvNative.DefinitionBytes, uvNative.ModelBytes, uvTextures);
            var uvPath = Path.Combine(root, "vanilla-tie.bin");
            await File.WriteAllBytesAsync(uvPath, canonicalUv);
            var vanillaUvPackage = await UyaRenderPackageService.BuildAssetPackageAsync(
                AssetId.Compute(AssetKind.Tie, UyaAssetImportService.CanonicalFormatVersion, canonicalUv),
                AssetKind.Tie, UyaAssetImportService.CanonicalFormatVersion,
                canonicalUv.Length, uvPath, CancellationToken.None);
            using (var uvDocument = JsonDocument.Parse(ReadPackageEntry(vanillaUvPackage, "model.gltf")))
            {
                var texCoords = ReadIndexedTexCoords(
                    uvDocument.RootElement, ReadPackageEntry(vanillaUvPackage, "model.buffer.bin"));
                Equal(true, texCoords.Max(value => value.U) - texCoords.Min(value => value.U) > 0.9f,
                    "catalog TIE rendering preserves authored UV spans like the custom preview");
            }

            var unsavedProjectPath = Path.Combine(root, "unsaved-project");
            var unsavedWorkspace = await CreateAsync(unsavedProjectPath);
            var unsaved = await UyaCustomTieProjectService.ImportAsync(unsavedWorkspace, Request(3));
            Equal(0, (await ForgeProjectWorkspace.OpenAsync(unsavedProjectPath))
                .Content.CustomModels.Models.Count, "direct import remains unsaved in memory");
            var placementPreview = await UyaAssetPreviewService.PrepareAsync(new(
                    previewCache, catalogPath, unsaved.Canonical.Id, AssetKind.Tie, "UYA", "model-default",
                    ProjectPath: unsavedProjectPath),
                "test-sdk", projectWorkspace: unsavedWorkspace);
            Equal(true, File.Exists(Path.Combine(placementPreview.RootPath, placementPreview.ModelPath)),
                "placement preview reads the active unsaved workspace without a reload");

            var classId = workspace.Content.CustomClasses.Allocations.Single().ClassId;
            EditorSnapshot placed;
            await using (var runtime = new EditorRuntime(placementResolver: UyaAssetPlacementService.CreateAsync))
            {
                await runtime.OpenAsync(projectPath, catalogPath, TimeSpan.Zero);
                placed = await runtime.ExecuteAsync(new(
                    Guid.NewGuid().ToString("D"), EditorCommandKind.CreateEntityFromAsset, [],
                    Placement: new(first.Canonical.Id, AssetKind.Tie, classId,
                        ProjectTransform.Identity with { Position = new(1, 2, 3) })));
                var firstPlaced = placed.Selection.Single();
                placed = await runtime.ExecuteAsync(new(
                    Guid.NewGuid().ToString("D"), EditorCommandKind.CreateEntityFromAsset, [],
                    Placement: new(first.Canonical.Id, AssetKind.Tie, classId,
                        ProjectTransform.Identity with { Position = new(4, 5, 6) })));
                var secondPlaced = placed.Selection.Single();
                placed = await runtime.ExecuteAsync(new(
                    Guid.NewGuid().ToString("D"), EditorCommandKind.UpdateTransform, [firstPlaced],
                    Transform: ProjectTransform.Identity with
                    {
                        Position = new(7, 8, 9),
                        Scale = new(2, 2, 2),
                    }));
                placed = await runtime.ExecuteAsync(new(
                    Guid.NewGuid().ToString("D"), EditorCommandKind.CreateGroup, [],
                    Text: "Custom TIE pair", GroupEdit: new()));
                var group = placed.Groups.Single(value => value.Name == "Custom TIE pair");
                placed = await runtime.ExecuteAsync(new(
                    Guid.NewGuid().ToString("D"), EditorCommandKind.AddGroupMembers, [firstPlaced, secondPlaced],
                    GroupEdit: new(group.GroupId)));
                placed = await runtime.ExecuteAsync(new(
                    Guid.NewGuid().ToString("D"), EditorCommandKind.SetEntityState, [secondPlaced],
                    State: new(Hidden: true, Locked: true)));
                _ = await runtime.ExecuteAsync(Command(EditorCommandKind.Undo));
                placed = await runtime.ExecuteAsync(Command(EditorCommandKind.Redo));
                await runtime.SaveAsync();
            }
            var firstEntity = placed.Groups.Single(value => value.Name == "Custom TIE pair").Members[0];
            var secondEntity = placed.Groups.Single(value => value.Name == "Custom TIE pair").Members[1];
            Equal(new ProjectVector3(7, 8, 9),
                placed.Entities.Single(value => value.EntityId == firstEntity).Transform.Position,
                "custom TIE uses ordinary transform editing");
            Equal(true, placed.Entities.Single(value => value.EntityId == secondEntity).State.Hidden,
                "custom TIE uses ordinary visibility and lock state");
            Equal(null, placed.Entities.Single(value => value.EntityId == firstEntity).InstancedCollision,
                "custom TIE discloses no assigned collision");
            Equal(first.Canonical.Id, placed.CustomModels.Single().AssetId,
                "custom TIE inventory is present in the editor snapshot");
            Equal(first.Native.Id, placed.CustomModels.Single().NativeAssetId,
                "custom TIE snapshot exposes its native preview revision");

            workspace = await ForgeProjectWorkspace.OpenAsync(projectPath);
            var collisionSource = await UyaInstancedCollisionPreviewService.InspectAsync(
                workspace, catalogPath, first.Canonical.Id, default);
            Equal(true, collisionSource.SurfaceLodIndices.Contains(0),
                "custom native TIE geometry is available to explicit collision tooling");
            await UyaStaticLayerStore.WriteSourceAsync(
                projectPath,
                new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)),
                [
                    new(BakeLayerId.Ties, UyaTieInstancesReader.RecordSize, [7, 8, 9], [0xaa]),
                    new(BakeLayerId.Shrubs, UyaShrubInstancesReader.RecordSize, [10, 11, 12], [0xbb]),
                    new(BakeLayerId.Mobys, UyaMobyInstancesReader.RecordSize, [0, 13, 14], [0xcc]),
                ]);
            var catalog = await AssetCatalogStore.OpenAsync(catalogPath);
            var inputs = await UyaStaticLayerStore.CreateBakeInputsAsync(projectPath, catalog);
            var context = new BakeFingerprintContext(
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), "translator-1", "baker-1");
            var plan = BakeLayerGraph.CreatePlan(context, inputs);
            var staging = await BakeStagingStore.OpenAsync(projectPath);
            var tiePlan = plan.Layers.Single(value => value.Layer == BakeLayerId.Ties);
            var staged = await UyaStaticLayerStore.StageAsync(projectPath, catalog, staging, tiePlan);
            var stagedManifest = ForgeProjectPersistence.Deserialize<UyaStaticBakeManifest>(
                await File.ReadAllBytesAsync(Path.Combine(staging.RootPath, staged.RelativePath, "manifest.json")),
                "custom TIE staged manifest");
            Equal(1, stagedManifest.Definitions.Count, "two custom instances share one staged definition");
            Equal(2, stagedManifest.Instances.Count, "both custom TIE instances are staged");
            Equal(classId, stagedManifest.Definitions.Single().ClassId,
                "staged custom definition uses the persisted native class");
            var stagedResource = await File.ReadAllBytesAsync(Path.Combine(
                staging.RootPath, staged.RelativePath, stagedManifest.Definitions.Single().Resource));
            var stagedCanonical = UyaCanonicalAssetCodec.Decode(stagedResource);
            Equal(native.DefinitionBytes.Length, stagedCanonical.DefinitionBytes.Length,
                "staged custom definition is semantically readable");
            Equal(native.ModelBytes.Length, stagedCanonical.ModelBytes.Length,
                "staged custom model is semantically readable");
            using var sourceIso = new MemoryStream(UyaProjectTests.CreateIso(), writable: false);
            var sourceLevelWad = UyaLooseLevelWadExtractor.ExtractPrimary(sourceIso, 3).Bytes;
            var customInput = new StaticAssetInput(
                stagedManifest.Definitions.Single().EffectiveAsset.Id.ToString(),
                TextureAssetFamily.Tie,
                classId,
                stagedCanonical.DefinitionBytes,
                stagedCanonical.ModelBytes,
                stagedCanonical.Textures.Select(texture => new StaticAssetTexture(
                    texture.Role == 0 ? TextureRole.Material : TextureRole.Billboard,
                    texture.PifBytes)).ToArray());
            var composedInputs = UyaLevelPackService.IncludeSourceStaticAssets(sourceLevelWad, [customInput]);
            var sourceAssets = UyaLevelWadRenderPackageBuilder.ReadAssetSourceFiles(
                UyaLevelWadUnpacker.Unpack(sourceLevelWad).Files);
            var composition = StaticAssetComposer.Compose(
                GameId.UYA,
                sourceAssets.HeaderBytes,
                sourceAssets.AssetWadBytes,
                sourceAssets.PaletteBytes,
                composedInputs);
            var composedHeader = LevelAssetReader.ReadHeader(composition.HeaderBytes);
            Equal(1, LevelAssetReader.ReadModelDefinitions(
                    composition.HeaderBytes, composedHeader.TieModelOffset, composedHeader.TieModelCount)
                .Count(definition => definition.ModelId == classId),
                "packed semantic read finds the custom class exactly once");
            var repeatedStage = await UyaStaticLayerStore.StageAsync(projectPath, catalog, staging, tiePlan);
            Equal(staged.OutputFingerprint, repeatedStage.OutputFingerprint,
                "custom TIE staging is deterministic");

            var beforeInputs = inputs.ToDictionary(value => value.Id);
            workspace.UpdateTransform(firstEntity,
                workspace.Content.Entities.Single(value => value.EntityId == firstEntity).Transform with
                {
                    Position = new(10, 11, 12),
                });
            await workspace.SaveAsync();
            var afterInputs = (await UyaStaticLayerStore.CreateBakeInputsAsync(projectPath, catalog))
                .ToDictionary(value => value.Id);
            Equal(false, beforeInputs[BakeLayerId.Ties].AuthoritativeContent.Span.SequenceEqual(
                    afterInputs[BakeLayerId.Ties].AuthoritativeContent.Span),
                "custom instance edits invalidate TIE content");
            Equal(true, beforeInputs[BakeLayerId.Shrubs].AuthoritativeContent.Span.SequenceEqual(
                    afterInputs[BakeLayerId.Shrubs].AuthoritativeContent.Span),
                "custom instance edits do not invalidate shrub content");
            Equal(true, beforeInputs[BakeLayerId.Mobys].AuthoritativeContent.Span.SequenceEqual(
                    afterInputs[BakeLayerId.Mobys].AuthoritativeContent.Span),
                "custom instance edits do not invalidate moby content");

            var nativePath = workspace.ResolveAttachedAssetPath(first.Native.Id)
                ?? throw new InvalidOperationException("Custom native asset path is missing.");
            var retainedManifest = staging.Manifest;
            try
            {
                await File.WriteAllBytesAsync(nativePath, "corrupt"u8.ToArray());
                await ThrowsAsync<InvalidDataException>(() => UyaStaticLayerStore.StageAsync(
                    projectPath, catalog, staging, tiePlan));
                Equal(retainedManifest, staging.Manifest, "failed custom stage retains the prior manifest");
            }
            finally
            {
                await File.WriteAllBytesAsync(nativePath, nativeBytes);
            }

            var secondRequest = Request(2);
            await using (var runtime = new EditorRuntime(
                customAssetCommandExecutor: UyaCustomAssetProjectService.ExecuteAsync))
            {
                await runtime.OpenAsync(projectPath, TimeSpan.Zero);
                await runtime.ExecuteAsync(Command(
                    EditorCommandKind.ReimportCustomTie,
                    new(first.Canonical.Id, secondRequest),
                    [firstEntity]));
                await runtime.SaveAsync();
                var changed = await ForgeProjectWorkspace.OpenAsync(projectPath);
                Equal(2, changed.Content.CustomModels.Models.Count,
                    "partial reimport retains the still-referenced old model");
                var replacement = changed.Content.Entities.Single(entity => entity.EntityId == firstEntity).Asset!;
                Equal(false, replacement.Id == first.Canonical.Id, "explicit dependent is retargeted");
                Equal(changed.Content.CustomClasses.Allocations.Single(value => value.Asset == replacement).ClassId,
                    changed.Content.Entities.Single(entity => entity.EntityId == firstEntity).Source!.ClassId,
                    "reimport rewrites the dependent instance to its persisted native class");
                Equal(first.Canonical,
                    changed.Content.Entities.Single(entity => entity.EntityId == secondEntity).Asset,
                    "unlisted dependent remains on the prior model");
                Equal(1, changed.Content.Assets.Count(asset => asset.Kind == AssetKind.Texture),
                    "reimport reuses identical converted texture content");

                await runtime.ExecuteAsync(Command(EditorCommandKind.Undo));
                await runtime.SaveAsync();
                Equal(1, (await ForgeProjectWorkspace.OpenAsync(projectPath)).Content.CustomModels.Models.Count,
                    "reimport undo restores the exact prior model state");
                await runtime.ExecuteAsync(Command(EditorCommandKind.Redo));
                await runtime.SaveAsync();
            }

            workspace = await ForgeProjectWorkspace.OpenAsync(projectPath);
            var second = workspace.Content.CustomModels.Models.Single(model => model.Canonical.Id != first.Canonical.Id);
            var report = workspace.InspectCustomModelDelete(first.Canonical.Id);
            Equal(true, report.ClassInstances.SequenceEqual([secondEntity]),
                "delete report lists every remaining class instance");
            await ThrowsAsync<ProjectCustomModelDeleteBlockedException>(() =>
                UyaCustomAssetProjectService.ExecuteAsync(workspace,
                    Command(EditorCommandKind.DeleteCustomTie, new(first.Canonical.Id)), default));

            workspace.Rename("Custom TIE recovery");
            _ = await workspace.WriteRecoveryAsync()
                ?? throw new InvalidOperationException("Expected custom model recovery snapshot.");
            await workspace.SaveAsync();
            _ = workspace.RemoveEntities([firstEntity, secondEntity]);
            await UyaCustomAssetProjectService.ExecuteAsync(workspace,
                Command(EditorCommandKind.DeleteCustomTie, new(first.Canonical.Id)), default);
            await UyaCustomAssetProjectService.ExecuteAsync(workspace,
                Command(EditorCommandKind.DeleteCustomTie, new(second.Canonical.Id)), default);
            await workspace.SaveAsync();
            Equal(0, (await workspace.CollectUnreferencedAssetsAsync()).Count,
                "recovery snapshot retains immutable model blobs");

            var sourceDirectory = Path.Combine(root, "original-source");
            Directory.CreateDirectory(sourceDirectory);
            await File.WriteAllBytesAsync(Path.Combine(sourceDirectory, "model.gltf"), firstRequest.SourceBytes);
            Directory.Delete(sourceDirectory, recursive: true);
            var zipPath = Path.Combine(root, "project.zip");
            ZipFile.CreateFromDirectory(projectPath, zipPath);
            var extracted = Path.Combine(root, "extracted");
            ZipFile.ExtractToDirectory(zipPath, extracted);
            var moved = await ForgeProjectWorkspace.OpenAsync(extracted);
            Equal(0, moved.Content.CustomModels.Models.Count,
                "zipped project reopens after original source removal");

            Directory.Delete(Path.Combine(projectPath, ForgeProjectWorkspace.RecoveryDirectoryName), recursive: true);
            var collected = await workspace.CollectUnreferencedAssetsAsync();
            Equal(true, collected.Count > 0, "ordinary cleanup collects history-free custom model blobs");
            Equal(0, workspace.Content.CustomClasses.Allocations.Count,
                "cleanup releases unused imported class allocations");

            var corrupt = await ForgeProjectWorkspace.OpenAsync(extracted);
            // The extracted copy still carries recovery-protected blobs even though its active model list is empty.
            var recovery = (await corrupt.ListRecoveriesAsync()).Single();
            await corrupt.LoadRecoveryAsync(recovery.Id);
            var recoveredModel = corrupt.Content.CustomModels.Models[0];
            var value = recoveredModel.Native.Id.ToString();
            await File.WriteAllBytesAsync(
                Path.Combine(extracted, "assets", value[..2], $"{value}.blob"), "corrupt"u8.ToArray());
            await ThrowsAsync<InvalidDataException>(() => UyaCustomTieProjectService.ImportAsync(corrupt, firstRequest));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<ForgeProjectWorkspace> CreateAsync(string path) =>
        await ForgeProjectWorkspace.CreateAsync(
            path,
            "Custom TIE project",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
            new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)),
            []);

    private static ProjectEntity Entity(EntityId id, string name, ProjectAssetReference asset) =>
        new(id, name, "ties", ProjectTransform.Identity, asset);

    private static EditorCommand Command(
        EditorCommandKind kind,
        EditorCustomModelEdit? edit = null,
        IReadOnlyList<EntityId>? entityIds = null) =>
        new(Guid.NewGuid().ToString("D"), kind, entityIds ?? [], CustomModelEdit: edit);

    private static ProjectCustomModelImportRequest Request(float xOffset, bool forceBackfaceCulling = false)
    {
        var geometry = new byte[42];
        var positions = new[] { xOffset, 0f, 0f, xOffset + 1f, 0f, 0f, xOffset, 1f, 0f };
        for (var index = 0; index < positions.Length; index++)
            BinaryPrimitives.WriteSingleLittleEndian(geometry.AsSpan(index * sizeof(float)), positions[index]);
        BinaryPrimitives.WriteUInt16LittleEndian(geometry.AsSpan(36), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(geometry.AsSpan(38), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(geometry.AsSpan(40), 2);
        var encoded = Convert.ToBase64String(geometry);
        var json = $$"""
            {
              "asset":{"version":"2.0"},
              "buffers":[{"uri":"data:application/octet-stream;base64,{{encoded}}","byteLength":42}],
              "bufferViews":[
                {"buffer":0,"byteOffset":0,"byteLength":36},
                {"buffer":0,"byteOffset":36,"byteLength":6}
              ],
              "accessors":[
                {"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},
                {"bufferView":1,"componentType":5123,"count":3,"type":"SCALAR"}
              ],
              "materials":[{"doubleSided":true}],
              "meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1,"material":0,"mode":4}]}],
              "nodes":[{"mesh":0}],
              "scenes":[{"nodes":[0]}],
              "scene":0
            }
            """;
        return new("model.gltf", Encoding.UTF8.GetBytes(json), [], forceBackfaceCulling);
    }

    private static ProjectCustomModelImportRequest UvRequest()
    {
        var geometry = new byte[66];
        var values = new[]
        {
            0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f,
            0.1f, 0f, 1.1f, 0f, 0.2f, 1f,
        };
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteSingleLittleEndian(geometry.AsSpan(index * sizeof(float)), values[index]);
        BinaryPrimitives.WriteUInt16LittleEndian(geometry.AsSpan(60), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(geometry.AsSpan(62), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(geometry.AsSpan(64), 2);
        var encoded = Convert.ToBase64String(geometry);
        var json = $$"""
            {
              "asset":{"version":"2.0"},
              "buffers":[{"uri":"data:application/octet-stream;base64,{{encoded}}","byteLength":66}],
              "bufferViews":[
                {"buffer":0,"byteOffset":0,"byteLength":36},
                {"buffer":0,"byteOffset":36,"byteLength":24},
                {"buffer":0,"byteOffset":60,"byteLength":6}
              ],
              "accessors":[
                {"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},
                {"bufferView":1,"componentType":5126,"count":3,"type":"VEC2"},
                {"bufferView":2,"componentType":5123,"count":3,"type":"SCALAR"}
              ],
              "meshes":[{"primitives":[{"attributes":{"POSITION":0,"TEXCOORD_0":1},"indices":2,"mode":4}]}],
              "nodes":[{"mesh":0}],
              "scenes":[{"nodes":[0]}],
              "scene":0
            }
            """;
        return new("uv-model.gltf", Encoding.UTF8.GetBytes(json), []);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
            throw new InvalidOperationException($"Expected {typeof(T).Name}.");
        }
        catch (T)
        {
        }
    }

    private static JsonDocument ReadGlbJson(byte[] bytes)
    {
        if (bytes.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x46546C67)
            throw new InvalidDataException("GLB header is invalid.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        return JsonDocument.Parse(bytes.AsMemory(20, length));
    }

    private static IReadOnlyList<(float U, float V)> ReadIndexedTexCoords(
        JsonElement root,
        byte[] buffer)
    {
        var primitive = root.GetProperty("meshes")[0].GetProperty("primitives")[0];
        var accessors = root.GetProperty("accessors");
        var views = root.GetProperty("bufferViews");
        var texCoordAccessor = accessors[primitive.GetProperty("attributes").GetProperty("TEXCOORD_0").GetInt32()];
        var texCoordView = views[texCoordAccessor.GetProperty("bufferView").GetInt32()];
        var texCoordOffset = texCoordView.GetProperty("byteOffset").GetInt32()
            + (texCoordAccessor.TryGetProperty("byteOffset", out var uvOffset) ? uvOffset.GetInt32() : 0);
        var indexAccessor = accessors[primitive.GetProperty("indices").GetInt32()];
        var indexView = views[indexAccessor.GetProperty("bufferView").GetInt32()];
        var indexOffset = indexView.GetProperty("byteOffset").GetInt32()
            + (indexAccessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0);
        var count = indexAccessor.GetProperty("count").GetInt32();
        return Enumerable.Range(0, count).Select(index =>
        {
            var vertex = checked((int)BitConverter.ToUInt32(buffer, indexOffset + index * sizeof(uint)));
            return (
                BitConverter.ToSingle(buffer, texCoordOffset + vertex * sizeof(float) * 2),
                BitConverter.ToSingle(buffer, texCoordOffset + vertex * sizeof(float) * 2 + sizeof(float)));
        }).ToArray();
    }

    private static byte[] ReadPackageEntry(PackedFilePackage package, string path)
    {
        var entry = package.Entries.Single(value => value.Path == path);
        return package.PackedBytes.AsSpan(entry.Offset, entry.Length).ToArray();
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}

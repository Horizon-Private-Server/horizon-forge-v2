using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Tfrags;
using RatchetPs2.Core.Wad;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaTfragProjectTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-uya-tfrag-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await VerifyImportAndPortabilityAsync(root);
            await VerifyInvalidImportIsAtomicAsync(root);
            await VerifySourceHydrationAsync(root);
            await VerifyMetadataHydrationPreservesEditsAsync(root);
            await VerifyBakeCompositionAsync(root);
            await VerifyDonorOwnerImportAsync(root);
            await VerifyCustomImportAsync(root);
            await VerifyInstancedCollisionAsync(root);
            VerifyStreamedPayloadReservation();
            VerifyOptionalPrimaryTerrain();
            await VerifyMissingSourceAsync(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyStreamedPayloadReservation()
    {
        var source = new byte[] { 1, 2, 3 };
        Equal<ReadOnlyMemory<byte>?>(null,
            UyaLevelPackService.ExpandPrimaryStreamBuffer(null, source, source.Length),
            "an existing primary streaming buffer should remain unchanged");

        var replacement = new byte[] { 4, 5 };
        var expanded = UyaLevelPackService.ExpandPrimaryStreamBuffer(replacement, source, 5)
            ?? throw new InvalidOperationException("a growing chunk should expand the primary streaming buffer");
        Equal(new byte[] { 4, 5, 0, 0, 0 }, expanded.ToArray(),
            "the primary streaming buffer should retain its payload and pad through the largest chunk");
    }

    private static void VerifyOptionalPrimaryTerrain()
    {
        Equal<TfragTerrain?>(null, UyaBaseLayerService.ReadPrimaryTerrain(new byte[0x40]),
            "an all-zero primary terrain placeholder should normalize to no terrain");
        var emptyTerrain = new byte[0x40];
        BinaryPrimitives.WriteInt32LittleEndian(emptyTerrain, 0x40);
        Equal(0, UyaBaseLayerService.ReadPrimaryTerrain(emptyTerrain)?.TfragCount,
            "a structurally valid empty terrain should still be parsed");
    }

    private static async Task VerifyCustomImportAsync(string root)
    {
        var projectPath = Path.Combine(root, "custom-import");
        var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "custom-import-catalog"));
        var workspace = await CreateWorkspaceAsync(projectPath);
        var payloads = new[] { Payload() };
        var source = new OpaqueContentSource("UYA", "NTSC-U", "1.00", 3, new string('a', 32));
        var entries = await catalog.PutManyAsync(UyaBaseLayerService.CreateCatalogPuts(payloads, source, "test"));
        await UyaBaseLayerStore.WriteAsync(projectPath, source, payloads, entries);
        await UyaTfragProjectService.ApplyAsync(workspace, UyaTfragProjectService.Create(payloads, 3));

        var originalCount = workspace.Content.Tfrags!.Sources.Single(value =>
            value.SourceKind == ProjectTfragSourceKind.Primary).GroupCount;
        var request = CustomTfragRequest();
        var importedIds = await UyaCustomTfragProjectService.ImportAsync(workspace, request);
        Equal(true, importedIds.Count > 0, "custom tfrag import creates partition entities");
        Equal(1, workspace.Content.Tfrags.CustomImports.Count,
            "custom tfrag import retains one source manifest");
        Equal(true, workspace.Content.Tfrags.CustomImports[0].Textures.Count > 0,
            "custom tfrag import retains converted terrain textures");
        var attachedImport = workspace.Content.Tfrags.CustomImports[0];
        Equal(true, attachedImport.Dependencies.Select(value => value.Asset.Id)
                .Prepend(attachedImport.Source.Id)
                .Concat(attachedImport.Textures.Select(value => value.Id))
                .All(workspace.IsAssetReferenced),
            "custom tfrag sources and textures are protected from attached-asset collection");
        Throws<InvalidDataException>(() => ForgeProjectValidation.Validate(
            workspace.RootPath, workspace.Manifest, workspace.Content with
        {
            Tfrags = workspace.Content.Tfrags with
            {
                CustomImports = [attachedImport, attachedImport],
            },
        }), "duplicate custom tfrag import IDs produce a project validation diagnostic");
        Throws<InvalidDataException>(() => ForgeProjectValidation.Validate(
            workspace.RootPath, workspace.Manifest, workspace.Content with
        {
            Tfrags = workspace.Content.Tfrags with
            {
                CustomImports =
                [
                    attachedImport with
                    {
                        Dependencies =
                        [
                            new("../escape.bin", ProjectCustomModelDependencyKind.Buffer, attachedImport.Source),
                        ],
                    },
                ],
            },
        }), "custom tfrag dependencies reject non-portable URIs");
        Equal(originalCount + importedIds.Count, workspace.Content.Tfrags.Sources.Single(value =>
            value.SourceKind == ProjectTfragSourceKind.Primary).GroupCount,
            "custom tfrag partitions extend the primary group inventory");
        Equal(true, importedIds.All(id => workspace.Content.Entities.Single(value => value.EntityId == id)
                .Tfrag is { CustomImportId: not null }),
            "custom tfrag entities retain their import ownership");
        await workspace.SaveAsync();
        Equal(0, (await workspace.CollectUnreferencedAssetsAsync()).Count,
            "attached-asset collection retains a complete custom tfrag import");

        var repeatedIds = await UyaCustomTfragProjectService.ImportAsync(workspace, request);
        Equal(importedIds.ToArray(), repeatedIds.ToArray(),
            "reimporting identical custom terrain replaces its generated pieces in place");
        Equal(1, workspace.Content.Tfrags.CustomImports.Count,
            "reimporting identical custom terrain does not create a duplicate manifest");

        var first = workspace.Content.Entities.Single(value => value.EntityId == repeatedIds[0]);
        var originalSourcePosition = first.Tfrag!.SourcePosition!;
        var movedTransform = first.Transform with
        {
            Position = new(first.Transform.Position.X + 7, first.Transform.Position.Y + 8,
                first.Transform.Position.Z + 9),
        };
        workspace.UpdateTransform(first.EntityId, movedTransform);
        var replacementRequest = CustomTfragRequest(0.5f);
        var replacementIds = await UyaCustomTfragProjectService.ImportAsync(workspace, replacementRequest);
        Equal(repeatedIds.ToArray(), replacementIds.ToArray(),
            "reimporting a changed source with the same name preserves matching piece identities");
        Equal(1, workspace.Content.Tfrags.CustomImports.Count,
            "reimporting a changed source replaces its prior manifest");
        var replacementFirst = workspace.Content.Entities.Single(value => value.EntityId == replacementIds[0]);
        Equal(
            new ProjectVector3(
                movedTransform.Position.X - originalSourcePosition.X,
                movedTransform.Position.Y - originalSourcePosition.Y,
                movedTransform.Position.Z - originalSourcePosition.Z),
            new ProjectVector3(
                replacementFirst.Transform.Position.X - replacementFirst.Tfrag!.SourcePosition!.X,
                replacementFirst.Transform.Position.Y - replacementFirst.Tfrag.SourcePosition.Y,
                replacementFirst.Transform.Position.Z - replacementFirst.Tfrag.SourcePosition.Z),
            "custom terrain reimport preserves the authored piece offset");

        var laterIds = await UyaCustomTfragProjectService.ImportAsync(
            workspace, CustomTfragRequest(0.3f, sourceName: "later-terrain.gltf"));
        var laterGroupBefore = workspace.Content.Entities.Single(value => value.EntityId == laterIds[0])
            .Tfrag!.GroupIndex;
        var resizedRequest = CustomTfragRequest(0.5f, size: 12);
        var resizedIds = await UyaCustomTfragProjectService.ImportAsync(workspace, resizedRequest);
        Equal(false, resizedIds.Count == replacementIds.Count,
            "changed partition counts exercise custom terrain range replacement");
        Equal(replacementIds.Take(Math.Min(replacementIds.Count, resizedIds.Count)).ToArray(),
            resizedIds.Take(Math.Min(replacementIds.Count, resizedIds.Count)).ToArray(),
            "resized custom terrain preserves every reusable piece identity");
        Equal(laterGroupBefore + resizedIds.Count - replacementIds.Count,
            workspace.Content.Entities.Single(value => value.EntityId == laterIds[0]).Tfrag!.GroupIndex,
            "resizing an earlier custom terrain import shifts later primary group indices");
        Equal(originalCount + resizedIds.Count + laterIds.Count, workspace.Content.Tfrags.Sources.Single(value =>
            value.SourceKind == ProjectTfragSourceKind.Primary).GroupCount,
            "custom terrain reimport keeps the source group inventory contiguous");

        var tfragRecord = (await UyaBaseLayerStore.InspectAsync(
                workspace, catalog, BakeLayerId.Tfrags, CancellationToken.None))
            .Manifest!.Layers.Single(value => value.Layer == BakeLayerId.Tfrags);
        var gameComposition = await UyaTfragBakeService.ComposeAsync(
            workspace, catalog, tfragRecord, CancellationToken.None);
        var previewComposition = await UyaTfragBakeService.ComposePreviewAsync(
            workspace, catalog, tfragRecord, CancellationToken.None);
        Equal(false, gameComposition["primary.bin"].SequenceEqual(previewComposition["primary.bin"]),
            "editor terrain composition leaves authored transforms for the scene projection to apply");

        var resizedFirst = workspace.Content.Entities.Single(value => value.EntityId == resizedIds[0]);
        var preview = await UyaAssetPreviewService.PrepareAsync(new(
            Path.Combine(root, "custom-import-preview"), catalog.RootPath, resizedFirst.Asset!.Id,
            AssetKind.Tfrag, "UYA", "model-default", ProjectPath: projectPath),
            "test-sdk", projectWorkspace: workspace);
        var previewBytes = await File.ReadAllBytesAsync(Path.Combine(preview.RootPath, preview.ModelPath));
        var previewJson = Encoding.UTF8.GetString(previewBytes);
        Equal(true, previewJson.Contains("model.buffer.bin", StringComparison.Ordinal)
            && previewJson.Contains("textures/texture-000.png", StringComparison.Ordinal)
            && File.Exists(Path.Combine(preview.RootPath, "model.buffer.bin"))
            && File.Exists(Path.Combine(preview.RootPath, "textures", "texture-000.png")),
            "custom tfrag preview exports the complete native target with converted textures");

        await workspace.SaveAsync();
        await using (var runtime = new EditorRuntime(
            transformCapabilityResolver: UyaEditorCapabilities.ResolveTransformCapabilities,
            customAssetCommandExecutor: UyaCustomAssetProjectService.ExecuteAsync))
        {
            var opened = await runtime.OpenAsync(projectPath, TimeSpan.Zero);
            Equal(true, runtime.HasCapability("editor.custom-tfrag.import"),
                "custom terrain import capability is advertised");
            Equal(EditorTransformCapabilities.Translate | EditorTransformCapabilities.Rotate
                    | EditorTransformCapabilities.Scale,
                opened.Entities.Single(entity => entity.EntityId == resizedIds[0]).TransformCapabilities,
                "custom terrain parents expose translation, rotation, and positive-uniform scale");
            var snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.DeleteEntities, [laterIds[0]]));
            Equal(true, laterIds.All(id => snapshot.Entities.All(entity => entity.EntityId != id)),
                "deleting one custom terrain piece removes its complete import");
            Equal(true, resizedIds.All(id => snapshot.Entities.Any(entity => entity.EntityId == id)),
                "custom terrain deletion leaves other imports intact");
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
            Equal(true, laterIds.All(id => snapshot.Entities.Any(entity => entity.EntityId == id)),
                "undo restores a deleted custom terrain import");
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
            Equal(true, laterIds.All(id => snapshot.Entities.All(entity => entity.EntityId != id)),
                "redo removes the complete custom terrain import again");
            await runtime.SaveAsync();
        }
        workspace = await ForgeProjectWorkspace.OpenAsync(projectPath);
        Equal(1, workspace.Content.Tfrags!.CustomImports.Count,
            "custom terrain deletion removes its source manifest");
        Equal(originalCount + resizedIds.Count, workspace.Content.Tfrags.Sources.Single(value =>
            value.SourceKind == ProjectTfragSourceKind.Primary).GroupCount,
            "custom terrain deletion compacts the source group inventory");

        var context = new BakeFingerprintContext(
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), "translator", "baker");
        var input = (await UyaBaseLayerStore.CreateBakeInputsAsync(projectPath, catalog))
            .Single(value => value.Id == BakeLayerId.Tfrags);
        var plan = BakeLayerGraph.CreatePlan(context, [input]).Layers
            .Single(value => value.Layer == BakeLayerId.Tfrags);
        var staging = await BakeStagingStore.OpenAsync(projectPath);
        var staged = await UyaBaseLayerStore.StageAsync(projectPath, catalog, staging, plan);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(
            projectPath, BakeStagingStore.StagingDirectoryName, staged.RelativePath, "primary.bin"));
        using var stream = new MemoryStream(bytes, writable: false);
        Equal(originalCount + resizedIds.Count, TfragTerrainReader.Read(stream).Chunks.Count,
            "custom tfrag staging excludes deleted terrain partitions");

        workspace = await ForgeProjectWorkspace.OpenAsync(projectPath);
        var vanillaIds = workspace.Content.Entities.Where(entity => entity.Tfrag is
                { CustomImportId: null, SourceKind: ProjectTfragSourceKind.Primary })
            .Select(entity => entity.EntityId).ToArray();
        await using (var runtime = new EditorRuntime(
            transformCapabilityResolver: UyaEditorCapabilities.ResolveTransformCapabilities,
            customAssetCommandExecutor: UyaCustomAssetProjectService.ExecuteAsync))
        {
            await runtime.OpenAsync(projectPath, TimeSpan.Zero);
            var snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.DeleteEntities, vanillaIds));
            Equal(true, vanillaIds.All(id => snapshot.Entities.All(entity => entity.EntityId != id)),
                "deleting the vanilla terrain source removes every native group");
            Equal(true, resizedIds.All(id => snapshot.Entities.Any(entity => entity.EntityId == id)),
                "deleting vanilla terrain preserves custom terrain imports");
            await runtime.SaveAsync();
        }
        workspace = await ForgeProjectWorkspace.OpenAsync(projectPath);
        Equal(resizedIds.Count, workspace.Content.Tfrags!.Sources.Single(value =>
                value.SourceKind == ProjectTfragSourceKind.Primary).GroupCount,
            "vanilla terrain deletion compacts custom partitions to the complete source inventory");
        var filteredInput = (await UyaBaseLayerStore.CreateBakeInputsAsync(projectPath, catalog))
            .Single(value => value.Id == BakeLayerId.Tfrags);
        var filteredPlan = BakeLayerGraph.CreatePlan(context, [filteredInput], staging.Manifest).Layers
            .Single(value => value.Layer == BakeLayerId.Tfrags);
        Equal(BakeLayerState.Dirty, filteredPlan.State,
            "vanilla terrain deletion invalidates the tfrag bake layer");
        var filtered = await UyaBaseLayerStore.StageAsync(
            projectPath, catalog, staging, filteredPlan, cancellationToken: CancellationToken.None);
        var filteredBytes = await File.ReadAllBytesAsync(Path.Combine(
            projectPath, BakeStagingStore.StagingDirectoryName, filtered.RelativePath, "primary.bin"));
        Equal(resizedIds.Count, TfragTerrainReader.Read(filteredBytes).Chunks.Count,
            "vanilla terrain deletion bakes a custom-only native terrain payload");
    }

    private static async Task VerifyDonorOwnerImportAsync(string root)
    {
        var projectPath = Path.Combine(root, "donor-owner-import");
        var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "donor-owner-catalog"));
        var workspace = await CreateWorkspaceAsync(projectPath);
        var targetPayloads = new[] { Payload() };
        var donorPayloads = new[] { Payload(), Payload(1), Payload(2) with { Bytes = SyntheticTerrain(0) } };
        var sourceIdentity = new OpaqueContentSource("UYA", "NTSC-U", "1.00", 3, new string('a', 32));
        var entries = await catalog.PutManyAsync(
            UyaBaseLayerService.CreateCatalogPuts(targetPayloads, sourceIdentity, "test"));
        await UyaBaseLayerStore.WriteAsync(projectPath, sourceIdentity, targetPayloads, entries);
        var target = UyaTfragProjectService.Create(targetPayloads, 3);
        await UyaTfragProjectService.ApplyAsync(workspace, target);
        var donor = UyaTfragProjectService.Create(donorPayloads, 4);
        var family = new ContentImportFamilyPlan(
            "tfrags", "Tfrags", ContentImportMode.AddAndReplace,
            ContentImportMode.Add, ContentImportSourceState.Present,
            true, true, true,
            ["source:tfrags:primary:0", "source:tfrags:chunk:1"], [], [], [], [],
            ["tfrags requires owner payload."], [], donorPayloads.Sum(value => (long)value.Bytes.Length), [], []);
        var plan = new ContentImportPlan(
            ContentImportPlanSchema.CurrentVersion, ContentImportCapabilitySchema.CurrentVersion,
            "import-" + new string('d', 32), "UYA", "NTSC-U", "1.00", new string('a', 32),
            new string('e', 64), 4, workspace.Manifest.ProjectId.ToString(), workspace.CurrentFingerprint,
            3, donorPayloads.Sum(value => (long)value.Bytes.Length), [family], [], []);
        var source = new UyaTfragContentImportSource(donor, donorPayloads, []);
        var first = UyaTfragContentImportService.PrepareCommit(workspace, source, plan, 1234);
        var repeated = UyaTfragContentImportService.PrepareCommit(workspace, source, plan, 1234);
        Equal(true, ForgeProjectPersistence.Serialize(first.Content).AsSpan()
                .SequenceEqual(ForgeProjectPersistence.Serialize(repeated.Content)),
            "donor tfrag import is deterministic");
        await workspace.CommitContentImportAsync(first);
        var firstRenderRevision = UyaCustomTfragRenderService.CreateRevision(workspace);

        Equal(2, workspace.Content.Tfrags!.ImportedOwners.Count,
            "nonempty primary and streamed donor owners retain native payload provenance");
        Equal(true, firstRenderRevision is not null,
            "donor tfrag owners invalidate the terrain render package");
        Equal(6, workspace.Content.Entities.Count(value => value.Tfrag is not null),
            "add preserves target groups and appends both donor owners");
        Equal(true, workspace.Content.Tfrags.ImportedOwners.All(value =>
                value.TargetKind == ProjectTfragSourceKind.Primary && value.TargetIndex == 0),
            "a donor chunk without a target chunk owner relocates into primary terrain");
        Equal(true, workspace.Content.Tfrags.ImportedOwners.SelectMany(value =>
                value.Pieces.Select(id => workspace.Content.Entities.Single(entity => entity.EntityId == id)))
            .All(value => value.Tfrag!.ImportedOwnerId is not null),
            "imported pieces retain atomic donor-owner membership");
        Equal(true, workspace.Content.Tfrags.ImportedOwners.Select(value => value.NativePayload.Id)
            .All(workspace.IsAssetReferenced),
            "raw donor owner payloads are protected from attached-asset collection");

        var record = (await UyaBaseLayerStore.InspectAsync(
                workspace, catalog, BakeLayerId.Tfrags, CancellationToken.None))
            .Manifest!.Layers.Single(value => value.Layer == BakeLayerId.Tfrags);
        var composed = await UyaTfragBakeService.ComposeAsync(workspace, catalog, record, CancellationToken.None);
        Equal(6, TfragTerrainReader.Read(composed["primary.bin"]).TfragCount,
            "primary and relocated donor chunk groups semantically re-read after composition");

        var remove = workspace.Content.Entities.Where(value => value.Tfrag is not null)
            .Select(value => $"entity:{value.EntityId}").Order(StringComparer.Ordinal).ToArray();
        var replaceFamily = family with { Mode = ContentImportMode.Replace, Remove = remove };
        var replacePlan = plan with
        {
            BatchId = "import-" + new string('f', 32),
            TargetFingerprint = workspace.CurrentFingerprint,
            Families = [replaceFamily],
        };
        var replace = UyaTfragContentImportService.PrepareCommit(workspace, source, replacePlan, 2345);
        Equal(0, replace.Attachments.Count,
            "reimport does not restage tfrag assets already owned by the project");
        Equal(true, replace.Batch.Reused.Any(value => value.StartsWith("asset:", StringComparison.Ordinal)),
            "reimport records project-owned tfrag assets as reused");
        Equal(false, replace.Batch.Added.Intersect(replace.Batch.Reused, StringComparer.Ordinal).Any(),
            "reimport batch does not report an asset as both added and reused");
        await workspace.CommitContentImportAsync(replace);
        Equal(false, firstRenderRevision == UyaCustomTfragRenderService.CreateRevision(workspace),
            "replaced donor tfrag owners receive a new terrain render revision");
        Equal(4, workspace.Content.Entities.Count(value => value.Tfrag is not null),
            "replace removes complete unprotected owners before importing donor groups");
        Equal(2, workspace.Content.Tfrags.ImportedOwners.Count,
            "replace leaves exactly one imported primary and streamed owner");
    }

    private static async Task VerifyInstancedCollisionAsync(string root)
    {
        var workspace = await CreateWorkspaceAsync(Path.Combine(root, "instanced-collision"));
        var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "instanced-collision-catalog"));
        var canonical = TfragPieceCanonicalCodec.Write(CollisionGeometry());
        var assetId = AssetId.Compute(
            AssetKind.Tfrag, ProjectTfragSchema.CanonicalFormatVersion, canonical);
        var entityId = EntityId.New();
        var chunkEntityId = EntityId.New();
        var transform = new ProjectTransform(
            new(10, 20, 30),
            new(0, 0, 0, 1),
            new(1, 1, 1));
        var entity = new ProjectEntity(
            entityId,
            "Tfrag Primary #0",
            "tfrags",
            transform,
            new(assetId, AssetKind.Tfrag),
            new("UYA", 3, "level_wad/terrain.bin", 0),
            new(),
            Tfrag: new(
                new string('a', 64),
                "level_wad/terrain.bin",
                ProjectTfragSourceKind.Primary,
                0,
                0,
                -1,
                0,
                [0]));
        var chunkEntity = entity with
        {
            EntityId = chunkEntityId,
            Name = "Tfrag Chunk 1 #0",
            Transform = transform with { Position = new(40, 50, 60) },
            Provenance = new("UYA", 3, "level_wad/chunks/chunk1.wad", 0),
            Tfrag = entity.Tfrag! with
            {
                OccurrenceKey = new string('b', 64),
                SourceArchive = "level_wad/chunks/chunk1.wad",
                SourceKind = ProjectTfragSourceKind.Chunk,
                SourceIndex = 1,
            },
        };
        await workspace.ImportTfragsAsync(
            new(ProjectTfragSchema.CurrentVersion,
            [
                new("level_wad/terrain.bin", ProjectTfragSourceKind.Primary, 0, 1),
                new("level_wad/chunks/chunk1.wad", ProjectTfragSourceKind.Chunk, 1, 1),
            ]),
            [entity, chunkEntity],
            [new(AssetKind.Tfrag, ProjectTfragSchema.CanonicalFormatVersion, canonical)]);

        var source = await UyaInstancedCollisionPreviewService.InspectAsync(
            workspace, catalog.RootPath, assetId, CancellationToken.None);
        Equal(true, source.SurfaceLodIndices.SequenceEqual([0, 1, 2]),
            "attached tfrag canonical assets expose collision source LODs");

        var candidate = CollisionWork.GenerateTfragDecimatedCandidate(
            canonical, GameId.UYA, "tfrag:test", rawType: 0x3d);
        var encoded = CollisionWork.EncodeStandalone(GameId.UYA, [candidate.Addition]);
        await workspace.ApplyInstancedCollisionProxyAsync(
            entityId,
            encoded.Bytes,
            0,
            new(
                ProjectInstancedCollisionRecipeKind.Surface,
                candidate.Recipe.Version,
                RecipeVersion: 1,
                candidate.Recipe.LodIndex,
                candidate.Recipe.RawType,
                TargetFaceRatio: candidate.Recipe.TargetFaceRatio));
        workspace.SetInstancedCollisionEnabled(entityId, false);
        await workspace.ApplyInstancedCollisionProxyAsync(
            chunkEntityId,
            encoded.Bytes,
            0,
            new(
                ProjectInstancedCollisionRecipeKind.Surface,
                candidate.Recipe.Version,
                RecipeVersion: 1,
                candidate.Recipe.LodIndex,
                candidate.Recipe.RawType,
                TargetFaceRatio: candidate.Recipe.TargetFaceRatio));
        workspace.SetInstancedCollisionEnabled(chunkEntityId, false);

        var additions = await UyaInstancedCollisionCompositionService.BuildAdditionsAsync(
            workspace,
            catalog,
            sourcePayloadIndex: 0,
            replacementEntityId: null,
            replacementSourceAssetId: null,
            replacementProxyBytes: null,
            new Dictionary<AssetId, CollisionSolidAddition>(),
            CancellationToken.None);
        Equal(1, additions.Count, "enabled tfrag collision contributes one native collision addition");
        Equal(true, additions[0].Faces.SelectMany(FaceVertices).Any(vertex =>
                vertex == new CollisionVertex(10, 20, 30)),
            "tfrag collision follows the editable piece transform in world space");
        var chunkAdditions = await UyaInstancedCollisionCompositionService.BuildAdditionsAsync(
            workspace,
            catalog,
            sourcePayloadIndex: 1,
            replacementEntityId: null,
            replacementSourceAssetId: null,
            replacementProxyBytes: null,
            new Dictionary<AssetId, CollisionSolidAddition>(),
            CancellationToken.None);
        Equal(1, chunkAdditions.Count, "streamed tfrag collision contributes only to its chunk payload");
        Equal(true, chunkAdditions[0].Faces.SelectMany(FaceVertices).Any(vertex =>
                vertex == new CollisionVertex(40, 50, 60)),
            "streamed tfrag collision retains its world transform in its chunk payload");

        await workspace.SaveAsync();
        var reopened = await ForgeProjectWorkspace.OpenAsync(workspace.RootPath);
        Equal(false, reopened.Content.Entities.Single(value => value.EntityId == entityId).InstancedCollisionEnabled,
            "tfrag collision mode survives save and reopen");
        var reopenedBindingEntityIds = reopened.Content.InstancedCollisionBindings
            .Select(value => value.InstanceEntityId).ToHashSet();
        Equal(true, reopenedBindingEntityIds.Count == 2
                && reopenedBindingEntityIds.Contains(entityId)
                && reopenedBindingEntityIds.Contains(chunkEntityId),
            "individual tfrag collision bindings survive save and reopen");

        workspace = reopened;
        var baseCollision = CollisionWork.EncodeStandalone(GameId.UYA,
        [
            new("base",
            [
                new(0x0f,
                    new(-100, -100, -100),
                    new(-99, -100, -100),
                    new(-100, -99, -100),
                    default,
                    IsQuad: false),
            ]),
        ]).Bytes;
        var collisionPayloads = new[]
        {
            new UyaBaseLayerPayload(BakeLayerId.Collision, "collision.bin", AssetKind.Collision,
                "level_wad/assets/asset_wad.bin", 0, baseCollision),
            new UyaBaseLayerPayload(BakeLayerId.Collision, "chunk-1.bin", AssetKind.Collision,
                "level_wad/chunks/chunk1.wad", 1, baseCollision),
        };
        var sourceMetadata = new OpaqueContentSource("UYA", "NTSC-U", "1.00", 3, new string('a', 32));
        var collisionEntries = await catalog.PutManyAsync(
            UyaBaseLayerService.CreateCatalogPuts(collisionPayloads, sourceMetadata, "test"));
        await UyaBaseLayerStore.WriteAsync(
            workspace.RootPath, sourceMetadata, collisionPayloads, collisionEntries);
        var collisionAssetId = collisionEntries[0].Id;
        var collisionPiece = CollisionConverter.Inspect(baseCollision, GameId.UYA).Pieces.Single();
        foreach (var sourcePayloadIndex in new[] { 0, 1 })
            workspace.AddEntity(new(
                EntityId.New(),
                $"Collision {sourcePayloadIndex}",
                "collision",
                ProjectTransform.Identity,
                new(collisionAssetId, AssetKind.Collision),
                new("UYA", 3, $"collision/{sourcePayloadIndex}", collisionPiece.SourcePieceIndex),
                Collision: new(
                    ProjectCollisionPieceKind.Solid,
                    sourcePayloadIndex,
                    collisionPiece.SourcePieceIndex,
                    collisionPiece.FaceCount,
                    collisionPiece.VertexCount,
                    collisionPiece.Types.Select(value =>
                        new ProjectCollisionTypeCount(value.RawType, value.Count)).ToArray())));
        await workspace.SaveAsync();

        var context = new BakeFingerprintContext(
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), "translator", "baker");
        var input = (await UyaBaseLayerStore.CreateBakeInputsAsync(workspace.RootPath, catalog))
            .Single(value => value.Id == BakeLayerId.Collision);
        var plan = BakeLayerGraph.CreatePlan(context, [input]).Layers
            .Single(value => value.Layer == BakeLayerId.Collision);
        var staging = await BakeStagingStore.OpenAsync(workspace.RootPath);
        var staged = await UyaBaseLayerStore.StageAsync(
            workspace.RootPath, catalog, staging, plan, CancellationToken.None);
        var stagedRoot = Path.Combine(
            workspace.RootPath, BakeStagingStore.StagingDirectoryName, staged.RelativePath);
        var primaryFaces = CollisionWork.DecodeSolidAddition(
            await File.ReadAllBytesAsync(Path.Combine(stagedRoot, "collision.bin")),
            GameId.UYA,
            "primary").Faces;
        var chunkFaces = CollisionWork.DecodeSolidAddition(
            await File.ReadAllBytesAsync(Path.Combine(stagedRoot, "chunk-1.bin")),
            GameId.UYA,
            "chunk-1").Faces;
        Equal(true, primaryFaces.SelectMany(FaceVertices).Contains(new CollisionVertex(10, 20, 30))
                && !primaryFaces.SelectMany(FaceVertices).Contains(new CollisionVertex(40, 50, 60)),
            "primary staging contains only primary tfrag generated collision");
        Equal(true, chunkFaces.SelectMany(FaceVertices).Contains(new CollisionVertex(40, 50, 60))
                && !chunkFaces.SelectMany(FaceVertices).Contains(new CollisionVertex(10, 20, 30)),
            "streamed staging contains only its own tfrag generated collision");

        var stagedChunkCollision = await File.ReadAllBytesAsync(Path.Combine(stagedRoot, "chunk-1.bin"));
        var terrainBytes = Enumerable.Repeat((byte)0x44, 0x10).ToArray();
        var compressedCollision = WadCompression.CompressVerified(baseCollision).CompressedBytes;
        var collisionOffset = 0x20;
        var collisionEnd = (collisionOffset + compressedCollision.Length + 0x0f) & ~0x0f;
        var sourceChunk = new byte[collisionEnd];
        BinaryPrimitives.WriteInt32LittleEndian(sourceChunk.AsSpan(0x00), 0x10);
        BinaryPrimitives.WriteInt32LittleEndian(sourceChunk.AsSpan(0x04), collisionOffset);
        BinaryPrimitives.WriteInt32LittleEndian(sourceChunk.AsSpan(0x08), collisionEnd - 0x10);
        BinaryPrimitives.WriteInt32LittleEndian(sourceChunk.AsSpan(0x0c), collisionEnd - 0x08);
        terrainBytes.CopyTo(sourceChunk.AsSpan(0x10));
        compressedCollision.CopyTo(sourceChunk.AsSpan(collisionOffset));
        var editedTerrain = Enumerable.Repeat((byte)0x66, 0x19).ToArray();
        var terrainComposedChunk = LevelAssetComposer.ComposeTfragChunk(
            GameId.UYA,
            sourceChunk,
            editedTerrain);
        var composedChunk = LevelAssetComposer.ComposeTfragChunkCollision(
            GameId.UYA,
            terrainComposedChunk,
            stagedChunkCollision);
        Equal(true, TfragChunkWadReader.ReadTerrainPayload(composedChunk).SequenceEqual(editedTerrain)
                && TfragChunkWadReader.ReadCollisionPayload(composedChunk).SequenceEqual(stagedChunkCollision)
                && composedChunk.AsSpan(0x08, 0x08).SequenceEqual(sourceChunk.AsSpan(0x08, 0x08)),
            "packing streamed terrain and generated collision preserves both payloads and header padding");
    }

    private static IEnumerable<CollisionVertex> FaceVertices(CollisionSolidFace face)
    {
        yield return face.A;
        yield return face.B;
        yield return face.C;
        if (face.IsQuad) yield return face.D;
    }

    private static TfragPieceGeometry CollisionGeometry()
    {
        var vertices = new[]
        {
            CollisionVertex(0, 0, 0),
            CollisionVertex(4, 0, 0),
            CollisionVertex(0, 4, 0),
            CollisionVertex(0, 0, 4),
        };
        TfragPieceLod Lod(int index) => new(index,
        [
            new(new(0, false, false), vertices, [0u, 1u, 2u, 0u, 3u, 1u]),
        ]);
        return new(
            new(new(0, 0, 0), new(4, 4, 4), 7),
            [0],
            [Lod(0), Lod(1), Lod(2)]);
    }

    private static TfragPieceVertex CollisionVertex(float x, float y, float z) => new(
        new(x, y, z),
        new(0, 0, 1),
        new(0, 0),
        new(1, 1, 1, 1),
        0,
        new(1, 1, 1, 1),
        new(0, 0, 1),
        1);

    private static async Task VerifyInvalidImportIsAtomicAsync(string root)
    {
        var workspace = await CreateWorkspaceAsync(Path.Combine(root, "invalid-import"));
        var import = UyaTfragProjectService.Create([Payload()], 3);
        var invalid = import with
        {
            State = import.State with
            {
                Sources = import.State.Sources.Select(source => source with
                    { SourceArchive = "wrong/archive.bin" }).ToArray(),
            },
        };
        await ThrowsAsync<InvalidDataException>(() => UyaTfragProjectService.ApplyAsync(workspace, invalid),
            "tfrag occurrence provenance must match its source inventory");
        var duplicateOccurrenceKey = import with
        {
            Entities = import.Entities.Select(entity => entity with
            {
                Tfrag = entity.Tfrag! with
                {
                    OccurrenceKey = import.Entities[0].Tfrag!.OccurrenceKey,
                },
            }).ToArray(),
        };
        await ThrowsAsync<InvalidDataException>(
            () => UyaTfragProjectService.ApplyAsync(workspace, duplicateOccurrenceKey),
            "tfrag occurrence identities must be unique");
        Equal<ProjectTfragState?>(null, workspace.Content.Tfrags,
            "invalid imports do not commit tfrag state");
        Equal(0, workspace.Content.Entities.Count, "invalid imports do not commit partial entities");
        Equal(0, workspace.Content.Assets.Count, "invalid imports do not commit partial asset metadata");
        Equal(false, Directory.Exists(Path.Combine(workspace.RootPath, "assets")),
            "invalid imports are rejected before writing attached asset blobs");
    }

    private static async Task VerifyImportAndPortabilityAsync(string root)
    {
        var projectPath = Path.Combine(root, "imported");
        var workspace = await CreateWorkspaceAsync(projectPath);
        var import = UyaTfragProjectService.Create([Payload()], 3);
        await UyaTfragProjectService.ApplyAsync(workspace, import);

        Equal(2, workspace.Content.Entities.Count, "one project entity per source tfrag group");
        Equal(1, workspace.Content.Assets.Count, "identical source groups share one canonical asset");
        Equal(2, workspace.Content.Entities.Select(entity => entity.Tfrag!.OccurrenceKey).Distinct().Count(),
            "deduplicated tfrag occurrences retain distinct identities");
        Equal(new ProjectVector3(1, 2, 3), workspace.Content.Entities[0].Transform.Position,
            "tfrag world transform is imported from the native bounding sphere");
        Equal(new ProjectVector3(1, 2, 3), workspace.Content.Entities[0].Tfrag!.SourcePosition,
            "tfrag source position remains independent from its editable transform");
        Equal("level_wad/assets/asset_wad.bin", workspace.Content.Entities[0].Provenance!.Section,
            "tfrag source provenance is retained");
        Equal(true, workspace.Content.Entities[0].Tfrag!.Bounds is not null,
            "canonical tfrag bounds are retained for inspection");
        Equal(true, workspace.Content.Entities[0].Tfrag!.Diagnostics is not null,
            "tfrag decode diagnostics are retained for inspection");

        var entity = workspace.Content.Entities[0];
        var asset = entity.Asset!;
        var editedTransform = entity.Transform with
        {
            Position = new(7, 8, 9),
            Rotation = new(0, 0, 0.70710677f, 0.70710677f),
            Scale = new(2, 2, 2),
        };
        workspace.UpdateTransform(entity.EntityId, editedTransform);
        Equal(asset, workspace.Content.Entities[0].Asset,
            "editing an occurrence transform does not mutate canonical geometry identity");
        Throws<InvalidDataException>(() => workspace.UpdateTransform(
            entity.EntityId,
            editedTransform with { Scale = new(2, 1, 2) }),
            "tfrag non-uniform scale is rejected before mutation");
        Throws<InvalidDataException>(() => workspace.UpdateTransform(
            entity.EntityId,
            editedTransform with { Scale = new(-1, -1, -1) }),
            "tfrag mirrored scale is rejected before mutation");
        Equal(editedTransform, workspace.Content.Entities[0].Transform,
            "rejected tfrag transforms preserve authoritative state");
        Throws<InvalidOperationException>(() => workspace.RemoveEntity(entity.EntityId),
            "tfrag delete is unavailable before native composition");
        Throws<InvalidOperationException>(() => workspace.AddCopies([entity]),
            "tfrag duplicate is unavailable before native composition");
        Throws<InvalidOperationException>(() => workspace.SetEntityState([entity.EntityId], null, true, null),
            "tfrag disable is unavailable before native composition");
        Throws<InvalidOperationException>(() => workspace.SetEntityLayer([entity.EntityId], "other"),
            "tfrag layer changes are unavailable");

        await workspace.SaveAsync();
        var reopened = await ForgeProjectWorkspace.OpenAsync(projectPath);
        Equal(workspace.Content.Entities.Select(value => value.EntityId).ToArray(),
            reopened.Content.Entities.Select(value => value.EntityId).ToArray(),
            "tfrag entity IDs survive save and reopen");
        Equal(editedTransform, reopened.Content.Entities[0].Transform,
            "tfrag occurrence transforms survive save and reopen");
        Equal<ProjectVector3?>(new(1, 2, 3), reopened.Content.Entities[0].Tfrag!.SourcePosition,
            "tfrag source positions survive save and reopen independently");
        var recoveryTransform = editedTransform with { Position = new(13, 14, 15) };
        reopened.UpdateTransform(entity.EntityId, recoveryTransform);
        var recovery = await reopened.WriteRecoveryAsync()
            ?? throw new InvalidOperationException("Expected tfrag recovery snapshot.");
        reopened.UpdateTransform(entity.EntityId, editedTransform);
        await reopened.LoadRecoveryAsync(recovery.Id);
        Equal(recoveryTransform, reopened.Content.Entities[0].Transform,
            "tfrag occurrence transforms survive recovery restore");
        await using (var runtime = new EditorRuntime(
            transformCapabilityResolver: UyaEditorCapabilities.ResolveTransformCapabilities))
        {
            var snapshot = await runtime.OpenAsync(projectPath, TimeSpan.Zero);
            Equal(2, snapshot.Entities.Count,
                "tfrag occurrences are exposed to entity-aware terrain rendering");
            Equal(EditorTransformCapabilities.Translate | EditorTransformCapabilities.Rotate
                    | EditorTransformCapabilities.Scale,
                snapshot.Entities[0].TransformCapabilities,
                "tfrag pieces expose translate, rotate, and positive-uniform scale tools");
            var runtimeTransform = editedTransform with
            {
                Position = new(10, 11, 12),
                Scale = new(3, 3, 3),
            };
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"),
                EditorCommandKind.UpdateTransform,
                [entity.EntityId],
                runtimeTransform));
            Equal(runtimeTransform, snapshot.Entities.Single(value => value.EntityId == entity.EntityId).Transform,
                "tfrag transform commits through the ordinary editor command path");
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
            Equal(editedTransform, snapshot.Entities.Single(value => value.EntityId == entity.EntityId).Transform,
                "undo restores the previous tfrag transform");
            snapshot = await runtime.ExecuteAsync(new(
                Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
            Equal(runtimeTransform, snapshot.Entities.Single(value => value.EntityId == entity.EntityId).Transform,
                "redo restores the tfrag transform");
        }

        var movedPath = Path.Combine(root, "moved");
        Directory.Move(projectPath, movedPath);
        var moved = await ForgeProjectWorkspace.OpenAsync(movedPath);
        var bytes = await moved.ReadAttachedAssetVerifiedAsync(asset, ForgeProjectWorkspace.MaxAttachedAssetBytes);
        Equal(true, bytes.Length > 0, "tfrag canonical assets remain valid after moving the project");

        var archivePath = Path.Combine(root, "tfrag-project.zip");
        ZipFile.CreateFromDirectory(movedPath, archivePath, CompressionLevel.Fastest, false);
        var transferredPath = Path.Combine(root, "transferred");
        ZipFile.ExtractToDirectory(archivePath, transferredPath);
        var transferred = await ForgeProjectWorkspace.OpenAsync(transferredPath);
        foreach (var reference in transferred.Content.Entities
                     .Where(value => value.Tfrag is not null)
                     .Select(value => value.Asset!)
                     .Distinct())
            Equal(true, (await transferred.ReadAttachedAssetVerifiedAsync(
                reference, ForgeProjectWorkspace.MaxAttachedAssetBytes)).Length > 0,
                "ZIP-transferred tfrag assets resolve from project attachments");
    }

    private static async Task VerifySourceHydrationAsync(string root)
    {
        var projectPath = Path.Combine(root, "migration");
        var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
        var workspace = await CreateWorkspaceAsync(projectPath);
        var payloads = new[] { Payload() };
        var source = new OpaqueContentSource("UYA", "NTSC-U", "1.00", 3, new string('a', 32));
        var entries = await catalog.PutManyAsync(UyaBaseLayerService.CreateCatalogPuts(payloads, source, "test"));
        await UyaBaseLayerStore.WriteAsync(projectPath, source, payloads, entries);

        var diagnostic = await UyaTfragProjectService.HydrateAsync(workspace, catalog, CancellationToken.None);
        Equal(EditorDiagnosticSeverity.Info, diagnostic!.Severity, "verified source migrates legacy tfrags");
        Equal(2, workspace.Content.Entities.Count, "migration imports all source groups");
        await workspace.SaveAsync();
        var ids = workspace.Content.Entities.Select(entity => entity.EntityId).ToArray();
        var reopened = await ForgeProjectWorkspace.OpenAsync(projectPath);
        Equal(ids, reopened.Content.Entities.Select(entity => entity.EntityId).ToArray(),
            "migrated tfrag identities survive save and reopen");
        Equal<EditorDiagnostic?>(null,
            await UyaTfragProjectService.HydrateAsync(reopened, catalog, CancellationToken.None),
            "persisted tfrags are not imported twice");
    }

    private static async Task VerifyMissingSourceAsync(string root)
    {
        var workspace = await CreateWorkspaceAsync(Path.Combine(root, "missing-source"));
        var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "empty-catalog"));
        var diagnostic = await UyaTfragProjectService.HydrateAsync(workspace, catalog, CancellationToken.None);
        Equal(EditorDiagnosticSeverity.Warning, diagnostic!.Severity,
            "legacy projects report the verified-source migration prerequisite");
        Equal<ProjectTfragState?>(null, workspace.Content.Tfrags,
            "failed migration leaves legacy project terrain state untouched");
        Equal(0, workspace.Content.Entities.Count, "failed migration does not add partial entities");
    }

    private static async Task VerifyMetadataHydrationPreservesEditsAsync(string root)
    {
        var projectPath = Path.Combine(root, "metadata-migration");
        var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "metadata-catalog"));
        var workspace = await CreateWorkspaceAsync(projectPath);
        var payloads = new[] { Payload() };
        var source = new OpaqueContentSource("UYA", "NTSC-U", "1.00", 3, new string('a', 32));
        var entries = await catalog.PutManyAsync(UyaBaseLayerService.CreateCatalogPuts(payloads, source, "test"));
        await UyaBaseLayerStore.WriteAsync(projectPath, source, payloads, entries);
        var import = UyaTfragProjectService.Create(payloads, 3);
        var legacy = import with
        {
            Entities = import.Entities.Select(entity => entity with
            {
                Tfrag = entity.Tfrag! with { SourcePosition = null, Bounds = null, Diagnostics = null },
            }).ToArray(),
        };
        await UyaTfragProjectService.ApplyAsync(workspace, legacy);
        var entity = workspace.Content.Entities[0];
        var edited = entity.Transform with { Position = new(7, 8, 9) };
        workspace.UpdateTransform(entity.EntityId, edited);

        var diagnostic = await UyaTfragProjectService.HydrateAsync(workspace, catalog, CancellationToken.None);

        Equal(EditorDiagnosticSeverity.Info, diagnostic!.Severity, "missing tfrag metadata is refreshed from source");
        var refreshed = workspace.Content.Entities.Single(value => value.EntityId == entity.EntityId);
        Equal(edited, refreshed.Transform, "metadata refresh preserves the editable transform");
        Equal<ProjectVector3?>(new(1, 2, 3), refreshed.Tfrag!.SourcePosition,
            "metadata refresh restores the immutable source position");
        Equal(true, refreshed.Tfrag.Bounds is not null, "metadata refresh restores canonical bounds");
    }

    private static async Task VerifyBakeCompositionAsync(string root)
    {
        var projectPath = Path.Combine(root, "bake-composition");
        var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "bake-catalog"));
        var workspace = await CreateWorkspaceAsync(projectPath);
        var payloads = new[] { Payload(), Payload(1) };
        var source = new OpaqueContentSource("UYA", "NTSC-U", "1.00", 3, new string('a', 32));
        var entries = await catalog.PutManyAsync(UyaBaseLayerService.CreateCatalogPuts(payloads, source, "test"));
        await UyaBaseLayerStore.WriteAsync(projectPath, source, payloads, entries);
        await UyaTfragProjectService.ApplyAsync(workspace, UyaTfragProjectService.Create(payloads, 3));
        await workspace.SaveAsync();
        var context = new BakeFingerprintContext(
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), "translator", "baker");
        var staging = await BakeStagingStore.OpenAsync(projectPath);
        var initialInput = (await UyaBaseLayerStore.CreateBakeInputsAsync(projectPath, catalog))
            .Single(value => value.Id == BakeLayerId.Tfrags);
        var initialPlan = BakeLayerGraph.CreatePlan(context, [initialInput]).Layers
            .Single(value => value.Layer == BakeLayerId.Tfrags);
        var initial = await UyaBaseLayerStore.StageAsync(projectPath, catalog, staging, initialPlan);
        var initialBytes = await File.ReadAllBytesAsync(Path.Combine(
            projectPath, BakeStagingStore.StagingDirectoryName, initial.RelativePath, "primary.bin"));
        Equal(true, initialBytes.SequenceEqual(payloads[0].Bytes),
            "no-edit tfrag staging preserves the verified source payload byte-for-byte");
        var initialChunkBytes = await File.ReadAllBytesAsync(Path.Combine(
            projectPath, BakeStagingStore.StagingDirectoryName, initial.RelativePath, "chunk-1.bin"));
        Equal(true, initialChunkBytes.SequenceEqual(payloads[1].Bytes),
            "no-edit streamed tfrag staging preserves the verified source payload byte-for-byte");

        workspace = await ForgeProjectWorkspace.OpenAsync(projectPath);
        var piece = workspace.Content.Entities.Single(value =>
            value.Tfrag is { SourceKind: ProjectTfragSourceKind.Primary, GroupIndex: 0 });
        var moved = piece.Transform with
        {
            Position = piece.Transform.Position with { X = piece.Transform.Position.X + 1 },
            Rotation = new(0, 0, MathF.Sin(MathF.PI / 12), MathF.Cos(MathF.PI / 12)),
            Scale = new(1.05f, 1.05f, 1.05f),
        };
        workspace.UpdateTransform(piece.EntityId, moved);
        await workspace.SaveAsync();
        var editedInput = (await UyaBaseLayerStore.CreateBakeInputsAsync(projectPath, catalog))
            .Single(value => value.Id == BakeLayerId.Tfrags);
        Equal(false, initialInput.RelevantSettings.Span.SequenceEqual(editedInput.RelevantSettings.Span),
            "tfrag transforms participate in the bake fingerprint");
        var preparation = await UyaBakeValidationService.PrepareAsync(
            projectPath, catalog, context, null, rebuildAll: false, CancellationToken.None);
        var editedPlan = preparation.Validation.Plan.Layers
            .Single(value => value.Layer == BakeLayerId.Tfrags);
        Equal(BakeLayerState.Dirty, editedPlan.State, "an edited tfrag invalidates its bake layer");
        Equal(true, preparation.TfragCompositions is not null,
            "dirty tfrag preflight retains its validated composition for staging");
        Equal(true, preparation.TfragCompositions!.Keys.SequenceEqual(["primary.bin"]),
            "tfrag preflight retains only changed source payloads");
        var deferredPreparation = await UyaBakeValidationService.PrepareAsync(
            projectPath, catalog, context, null, false,
            new HashSet<BakeLayerId> { BakeLayerId.Hud }, CancellationToken.None);
        Equal<IReadOnlyDictionary<string, byte[]>?>(null, deferredPreparation.TfragCompositions,
            "partial bake does not compose a deferred dirty tfrag layer");
        Equal(BakeLayerState.Dirty,
            deferredPreparation.Validation.Plan.Layers.Single(value => value.Layer == BakeLayerId.Tfrags).State,
            "partial bake keeps deferred tfrags dirty");
        var edited = await UyaBaseLayerStore.StageAsync(
            projectPath, catalog, staging, editedPlan, preparation, CancellationToken.None);
        var editedBytes = await File.ReadAllBytesAsync(Path.Combine(
            projectPath, BakeStagingStore.StagingDirectoryName, edited.RelativePath, "primary.bin"));
        Equal(2048f, BitConverter.ToSingle(editedBytes, 0x40),
            "tfrag staging writes the edited native group center");
        Equal(true, editedBytes.AsSpan(0x80).SequenceEqual(payloads[0].Bytes.AsSpan(0x80)),
            "tfrag staging leaves unrelated records and group data byte-identical");
        var untouchedChunkBytes = await File.ReadAllBytesAsync(Path.Combine(
            projectPath, BakeStagingStore.StagingDirectoryName, edited.RelativePath, "chunk-1.bin"));
        Equal(true, untouchedChunkBytes.SequenceEqual(payloads[1].Bytes),
            "a primary tfrag edit leaves streamed terrain byte-identical");
        var cleanPreparation = await UyaBakeValidationService.PrepareAsync(
            projectPath, catalog, context, null, rebuildAll: false, CancellationToken.None);
        Equal<IReadOnlyDictionary<string, byte[]>?>(null, cleanPreparation.TfragCompositions,
            "clean tfrag preflight does not repeat semantic composition");
        var repeated = await UyaBaseLayerStore.StageAsync(projectPath, catalog, staging, editedPlan);
        Equal(edited.OutputFingerprint, repeated.OutputFingerprint,
            "repeated tfrag staging is deterministic");

        Directory.Delete(staging.RootPath, recursive: true);
        staging = await BakeStagingStore.OpenAsync(projectPath);
        var cleanPlan = BakeLayerGraph.CreatePlan(context, [editedInput], staging.Manifest).Layers
            .Single(value => value.Layer == BakeLayerId.Tfrags);
        var clean = await UyaBaseLayerStore.StageAsync(projectPath, catalog, staging, cleanPlan);
        Equal(edited.OutputFingerprint, clean.OutputFingerprint,
            "clean tfrag rebuild is deterministic");

        workspace = await ForgeProjectWorkspace.OpenAsync(projectPath);
        var chunkPiece = workspace.Content.Entities.Single(value =>
            value.Tfrag is { SourceKind: ProjectTfragSourceKind.Chunk, SourceIndex: 1, GroupIndex: 0 });
        workspace.UpdateTransform(chunkPiece.EntityId, chunkPiece.Transform with
        {
            Position = chunkPiece.Transform.Position with { Y = chunkPiece.Transform.Position.Y + 2 },
            Rotation = new(MathF.Sin(MathF.PI / 16), 0, 0, MathF.Cos(MathF.PI / 16)),
            Scale = new(0.75f, 0.75f, 0.75f),
        });
        await workspace.SaveAsync();
        var chunkInput = (await UyaBaseLayerStore.CreateBakeInputsAsync(projectPath, catalog))
            .Single(value => value.Id == BakeLayerId.Tfrags);
        var chunkPlan = BakeLayerGraph.CreatePlan(context, [chunkInput], staging.Manifest).Layers
            .Single(value => value.Layer == BakeLayerId.Tfrags);
        var chunkEdited = await UyaBaseLayerStore.StageAsync(projectPath, catalog, staging, chunkPlan);
        var preservedPrimaryBytes = await File.ReadAllBytesAsync(Path.Combine(
            projectPath, BakeStagingStore.StagingDirectoryName, chunkEdited.RelativePath, "primary.bin"));
        Equal(true, preservedPrimaryBytes.SequenceEqual(editedBytes),
            "a streamed tfrag edit preserves the composed primary terrain");
        var editedChunkBytes = await File.ReadAllBytesAsync(Path.Combine(
            projectPath, BakeStagingStore.StagingDirectoryName, chunkEdited.RelativePath, "chunk-1.bin"));
        Equal(4096f, BitConverter.ToSingle(editedChunkBytes, 0x44),
            "streamed tfrag staging writes the edited native group center");

        workspace = await ForgeProjectWorkspace.OpenAsync(projectPath);
        piece = workspace.Content.Entities.Single(value => value.EntityId == piece.EntityId);
        workspace.UpdateTransform(piece.EntityId, piece.Transform with
        {
            Position = piece.Transform.Position with { X = piece.Transform.Position.X + 1 },
        });
        await workspace.SaveAsync();
        var failedInput = (await UyaBaseLayerStore.CreateBakeInputsAsync(projectPath, catalog))
            .Single(value => value.Id == BakeLayerId.Tfrags);
        var stagingBeforeFailure = await BakeStagingStore.OpenAsync(projectPath);
        var manifestBeforeFailure = ForgeProjectPersistence.Serialize(stagingBeforeFailure.Manifest);
        var failedPlan = BakeLayerGraph.CreatePlan(context, [failedInput], stagingBeforeFailure.Manifest).Layers
            .Single(value => value.Layer == BakeLayerId.Tfrags);
        using (var cancellation = new CancellationTokenSource())
        {
            await ThrowsAsync<OperationCanceledException>(() => UyaBaseLayerStore.StageAsync(
                projectPath,
                catalog,
                stagingBeforeFailure,
                failedPlan,
                fault: _ => cancellation.Cancel(),
                cancellationToken: cancellation.Token),
                "cancelled tfrag staging is rejected");
            var manifestAfterCancellation = (await BakeStagingStore.OpenAsync(projectPath)).Manifest;
            Equal(true, manifestBeforeFailure.SequenceEqual(
                    ForgeProjectPersistence.Serialize(manifestAfterCancellation)),
                "cancelled tfrag staging preserves the last good stage");
        }
        await ThrowsAsync<InvalidDataException>(() => UyaBaseLayerStore.StageAsync(
            projectPath,
            catalog,
            stagingBeforeFailure,
            failedPlan,
            fault: output => File.WriteAllBytes(Path.Combine(output, "primary.bin"), [0]),
            cancellationToken: CancellationToken.None),
            "invalid tfrag staging output is rejected");
        var manifestAfterFailure = (await BakeStagingStore.OpenAsync(projectPath)).Manifest;
        Equal(true, manifestBeforeFailure.SequenceEqual(ForgeProjectPersistence.Serialize(manifestAfterFailure)),
            "invalid composed tfrags preserve the last good stage");
    }

    private static Task<ForgeProjectWorkspace> CreateWorkspaceAsync(string path) =>
        ForgeProjectWorkspace.CreateAsync(
            path,
            "Tfrag test",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
            new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)),
            []);

    private static ProjectCustomModelImportRequest CustomTfragRequest(
        float heightScale = 0.25f,
        int size = 4,
        string sourceName = "terrain.gltf")
    {
        var positions = new List<float>();
        var normals = new List<float>();
        var texCoords = new List<float>();
        for (var y = 0; y <= size; y++)
        {
            for (var x = 0; x <= size; x++)
            {
                positions.AddRange([x, y, MathF.Sin(x * 0.5f) * heightScale]);
                normals.AddRange([0, 0, 1]);
                texCoords.AddRange([x / (float)size, y / (float)size]);
            }
        }
        var indices = new List<ushort>();
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var topLeft = checked((ushort)(y * (size + 1) + x));
                var topRight = checked((ushort)(topLeft + 1));
                var bottomLeft = checked((ushort)((y + 1) * (size + 1) + x));
                var bottomRight = checked((ushort)(bottomLeft + 1));
                indices.AddRange([topLeft, topRight, bottomLeft, bottomLeft, topRight, bottomRight]);
            }
        }
        var positionBytes = positions.Count * sizeof(float);
        var normalOffset = positionBytes;
        var normalBytes = normals.Count * sizeof(float);
        var texCoordOffset = normalOffset + normalBytes;
        var texCoordBytes = texCoords.Count * sizeof(float);
        var indexOffset = texCoordOffset + texCoordBytes;
        var geometry = new byte[indexOffset + indices.Count * sizeof(ushort)];
        for (var index = 0; index < positions.Count; index++)
            BinaryPrimitives.WriteSingleLittleEndian(geometry.AsSpan(index * sizeof(float)), positions[index]);
        for (var index = 0; index < normals.Count; index++)
            BinaryPrimitives.WriteSingleLittleEndian(
                geometry.AsSpan(normalOffset + index * sizeof(float)), normals[index]);
        for (var index = 0; index < texCoords.Count; index++)
            BinaryPrimitives.WriteSingleLittleEndian(
                geometry.AsSpan(texCoordOffset + index * sizeof(float)), texCoords[index]);
        for (var index = 0; index < indices.Count; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(
                geometry.AsSpan(indexOffset + index * sizeof(ushort)), indices[index]);
        var encoded = Convert.ToBase64String(geometry);
        var json = $$"""
            {
              "asset":{"version":"2.0"},
              "buffers":[{"uri":"data:application/octet-stream;base64,{{encoded}}","byteLength":{{geometry.Length}}}],
              "bufferViews":[
                {"buffer":0,"byteOffset":0,"byteLength":{{positionBytes}}},
                {"buffer":0,"byteOffset":{{normalOffset}},"byteLength":{{normalBytes}}},
                {"buffer":0,"byteOffset":{{texCoordOffset}},"byteLength":{{texCoordBytes}}},
                {"buffer":0,"byteOffset":{{indexOffset}},"byteLength":{{indices.Count * sizeof(ushort)}}}
              ],
              "accessors":[
                {"bufferView":0,"componentType":5126,"count":{{positions.Count / 3}},"type":"VEC3"},
                {"bufferView":1,"componentType":5126,"count":{{normals.Count / 3}},"type":"VEC3"},
                {"bufferView":2,"componentType":5126,"count":{{texCoords.Count / 2}},"type":"VEC2"},
                {"bufferView":3,"componentType":5123,"count":{{indices.Count}},"type":"SCALAR"}
              ],
              "materials":[{"name":"terrain"}],
              "meshes":[{"primitives":[{
                "attributes":{"POSITION":0,"NORMAL":1,"TEXCOORD_0":2},"indices":3,"material":0,"mode":4
              }]}],
              "nodes":[{"mesh":0}],
              "scenes":[{"nodes":[0]}],
              "scene":0
            }
            """;
        return new(sourceName, Encoding.UTF8.GetBytes(json), []);
    }

    private static UyaBaseLayerPayload Payload(int sourceIndex = 0) => new(
        BakeLayerId.Tfrags,
        sourceIndex == 0 ? "primary.bin" : $"chunk-{sourceIndex}.bin",
        AssetKind.Tfrag,
        sourceIndex == 0 ? "level_wad/assets/asset_wad.bin" : $"level_wad/chunks/chunk{sourceIndex}.wad",
        sourceIndex,
        SyntheticTerrain());

    private static byte[] SyntheticTerrain(int groupCount = 2)
    {
        const int tableOffset = 0x40;
        const int recordSize = 0x40;
        const int dataSize = 0x10;
        var bytes = new byte[tableOffset + recordSize * groupCount + dataSize * groupCount];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x00, 4), tableOffset);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x04, 4), groupCount);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x08, 4), BitConverter.SingleToInt32Bits(32f));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x0C, 4), 2);
        for (var index = 0; index < groupCount; index++)
        {
            var record = tableOffset + index * recordSize;
            var data = tableOffset + recordSize * groupCount + index * dataSize;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(record, 4),
                BitConverter.SingleToInt32Bits((index * 3 + 1) * 1024f));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(record + 4, 4),
                BitConverter.SingleToInt32Bits((index * 3 + 2) * 1024f));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(record + 8, 4),
                BitConverter.SingleToInt32Bits((index * 3 + 3) * 1024f));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(record + 0x0C, 4),
                BitConverter.SingleToInt32Bits(32f));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(record + 0x10, 4), data - tableOffset);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(record + 0x2E, 2), -1);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(record + 0x30, 2), -1);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(record + 0x38, 2), -1);
            bytes.AsSpan(data, dataSize).Fill(0xA5);
        }
        return bytes;
    }

    private static void Throws<T>(Action action, string context) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"{context}: expected {typeof(T).Name}");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action, string context) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"{context}: expected {typeof(T).Name}");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (expected is Array expectedArray && actual is Array actualArray)
        {
            if (expectedArray.Cast<object>().SequenceEqual(actualArray.Cast<object>())) return;
        }
        else if (EqualityComparer<T>.Default.Equals(expected, actual))
        {
            return;
        }
        throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}

using System.Buffers.Binary;
using System.IO.Compression;
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
            await VerifyInstancedCollisionAsync(root);
            VerifyStreamedPayloadReservation();
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

    private static UyaBaseLayerPayload Payload(int sourceIndex = 0) => new(
        BakeLayerId.Tfrags,
        sourceIndex == 0 ? "primary.bin" : $"chunk-{sourceIndex}.bin",
        AssetKind.Tfrag,
        sourceIndex == 0 ? "level_wad/assets/asset_wad.bin" : $"level_wad/chunks/chunk{sourceIndex}.wad",
        sourceIndex,
        SyntheticTerrain());

    private static byte[] SyntheticTerrain()
    {
        const int tableOffset = 0x40;
        const int recordSize = 0x40;
        const int dataSize = 0x10;
        var bytes = new byte[tableOffset + recordSize * 2 + dataSize * 2];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x00, 4), tableOffset);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x04, 4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x08, 4), BitConverter.SingleToInt32Bits(32f));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x0C, 4), 2);
        for (var index = 0; index < 2; index++)
        {
            var record = tableOffset + index * recordSize;
            var data = tableOffset + recordSize * 2 + index * dataSize;
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

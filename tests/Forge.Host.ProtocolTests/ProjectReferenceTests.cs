using Forge.Host.Bridge;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;

internal static class ProjectReferenceTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-references-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var splineId = EntityId.New();
            var cuboidId = EntityId.New();
            var areaId = EntityId.New();
            var mobyId = EntityId.New();
            var mobyAssetId = AssetId.Parse(new string('a', AssetId.TextLength));
            var spline = new ProjectEntity(
                splineId, "Spline", "splines", ProjectTransform.Identity, null,
                Geometry: new(Spline: new([])));
            var cuboid = new ProjectEntity(
                cuboidId, "Cuboid", "cuboids", ProjectTransform.Identity, null,
                Geometry: new(Cuboid: new(
                    Enumerable.Repeat(0f, 16).ToArray(),
                    Enumerable.Repeat(0f, 12).ToArray(),
                    new(0, 0, 0))));
            var moby = new ProjectEntity(
                mobyId, "Moby", "mobys", ProjectTransform.Identity,
                new(mobyAssetId, AssetKind.Moby));
            var reference = new ProjectReference(
                ProjectReferenceDomain.Entity,
                ProjectReferences.AreaSplines,
                Nullable: true,
                EntityKind: ProjectEntityKind.Spline,
                EntityId: splineId);
            var area = Area(areaId, [new(7, Reference: reference)]);
            var levelSettings = new ProjectLevelSettings(
                new(1, 2, 3), new(4, 5, 6), 10, 175, 255, 0,
                new(0, false, new(0, 0, 0), new(0, 0, 0), 0,
                    IndexedReference(7, ProjectReferences.LevelShipPath, ProjectEntityKind.Spline, splineId),
                    IndexedReference(3, ProjectReferences.LevelShipCameraCuboidStart, ProjectEntityKind.Cuboid, cuboidId),
                    IndexedReference(3, ProjectReferences.LevelShipCameraCuboidEnd, ProjectEntityKind.Cuboid, cuboidId)));
            var workspace = await ForgeProjectWorkspace.CreateAsync(
                Path.Combine(root, "project"),
                "References",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 3, UyaIsoService.SupportedMd5),
                [spline, cuboid, moby, area],
                levelSettings);

            await using (var runtime = new EditorRuntime())
            {
                var snapshot = await runtime.OpenAsync(workspace.RootPath, TimeSpan.Zero);
                var bridged = snapshot.References.Single(value =>
                    value.Reference.FieldKey == ProjectReferences.AreaSplines);
                Equal(areaId, bridged.OwnerEntityId, "bridge reference owner");
                Equal(ProjectReferenceDomain.Entity, bridged.Reference.Domain, "bridge reference domain");
                Equal(ProjectEntityKind.Spline, bridged.Reference.EntityKind, "bridge reference target kind");
                Equal(splineId, bridged.Reference.EntityId, "bridge reference stable target");
                Equal(false, bridged.Missing, "bridge reference resolved state");
                var assetReference = snapshot.References.Single(value =>
                    value.Reference.FieldKey == ProjectReferences.EntityAsset);
                Equal(ProjectReferenceDomain.Asset, assetReference.Reference.Domain,
                    "bridge asset reference domain");
                Equal(AssetKind.Moby, assetReference.Reference.AssetKind,
                    "bridge asset reference target kind");
                Equal(mobyAssetId, assetReference.Reference.AssetId,
                    "bridge asset stable target");
                Equal(true, EditorPayloadCodec.EncodeSnapshot(snapshot).Length > 0,
                    "reference snapshot has a bridge representation");
                var editedLevelSettings = levelSettings with
                {
                    Scene = levelSettings.Scene! with { DeathHeight = -64 },
                };
                snapshot = await runtime.ExecuteAsync(new(
                    Guid.NewGuid().ToString("D"), EditorCommandKind.UpdateLevelSettings, [],
                    LevelSettings: editedLevelSettings));
                Equal(-64f, snapshot.LevelSettings!.Scene!.DeathHeight,
                    "level scene settings use the undoable command path");
                snapshot = await runtime.ExecuteAsync(new(
                    Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
                Equal(0f, snapshot.LevelSettings!.Scene!.DeathHeight,
                    "level scene setting command is undoable");
                await ThrowsAsync<InvalidDataException>(() => runtime.ExecuteAsync(new(
                    Guid.NewGuid().ToString("D"), EditorCommandKind.UpdateLevelSettings, [],
                    LevelSettings: levelSettings with
                    {
                        Scene = levelSettings.Scene! with
                        {
                            ShipPath = levelSettings.Scene.ShipPath with
                            {
                                Reference = levelSettings.Scene.ShipPath.Reference with { EntityId = mobyId },
                            },
                        },
                    })));
            }

            var graph = ProjectReferenceGraph.Create(workspace.Content);
            var edge = graph.Incoming(splineId).Single();
            Equal(areaId, edge.OwnerEntityId, "inbound edge owner");
            Equal(ProjectReferences.AreaSplines, edge.Reference.FieldKey, "inbound edge field");
            Equal(7, edge.SourceValue, "inbound edge raw source value");
            Equal(4, ProjectReferences.ResolveNativeIndex(edge, new Dictionary<EntityId, int> { [splineId] = 4 }),
                "stable ID resolves to final native index");
            var diagnostic = Throws<InvalidDataException>(() =>
                ProjectReferences.ResolveNativeIndex(edge, new Dictionary<EntityId, int>()));
            Equal(true, diagnostic.Message.Contains(areaId.ToString(), StringComparison.Ordinal)
                && diagnostic.Message.Contains(ProjectReferences.AreaSplines, StringComparison.Ordinal)
                && diagnostic.Message.Contains("raw value 7", StringComparison.Ordinal)
                && diagnostic.Message.Contains(splineId.ToString(), StringComparison.Ordinal),
                "failed native mapping reports owner, field, raw value, and target");
            var reorderedGraph = ProjectReferenceGraph.Create(workspace.Content with
            {
                Entities = workspace.Content.Entities.Reverse().ToArray(),
            });
            Equal(areaId, reorderedGraph.Incoming(splineId).Single().OwnerEntityId,
                "stable reference survives project list reorder");

            var beforeReferenceEdit = workspace.CaptureState();
            workspace.UpdateEntityReference(areaId, ProjectReferences.AreaSplines, 7, null);
            Equal(null, ProjectReferenceGraph.Create(workspace.Content).Outgoing(areaId).Single()
                .Reference.EntityId, "nullable reference can be cleared without losing its source slot");
            workspace.RestoreState(beforeReferenceEdit);

            var analysis = workspace.AnalyzeDelete([splineId]);
            Equal(1, analysis.ClearedReferences.Count, "delete analysis finds nullable inbound reference");
            Equal(0, analysis.BlockingReferences.Count, "nullable area link does not block delete");
            var history = new EditorHistory();
            var beforeDelete = workspace.CaptureState();
            _ = workspace.RemoveEntities([splineId]);
            Equal(-1, workspace.Content.LevelSettings!.Scene!.ShipPath.SourceValue,
                "delete clears the native source value for a level reference");
            Equal(null, workspace.Content.LevelSettings.Scene.ShipPath.Reference.EntityId,
                "delete clears the stable target for a level reference");
            var afterDelete = workspace.CaptureState();
            history.Push(beforeDelete, afterDelete, [], [], [splineId], 256);
            Equal(0, workspace.Content.Entities.Single(entity => entity.EntityId == areaId)
                .Geometry!.Area!.Splines.Count, "delete clears nullable area link atomically");
            Equal(true, history.TryUndo(out var state, out _, out _), "reference-clearing delete is undoable");
            workspace.RestoreState(state);
            Equal(splineId, workspace.Content.LevelSettings!.Scene!.ShipPath.Reference.EntityId,
                "undo restores a cleared level reference");
            Equal(splineId, workspace.Content.Entities.Single(entity => entity.EntityId == areaId)
                .Geometry!.Area!.Splines.Single().Reference!.EntityId, "undo restores target and link");
            Equal(true, history.TryRedo(out state, out _, out _), "reference-clearing delete is redoable");
            workspace.RestoreState(state);
            Equal(false, workspace.Content.Entities.Any(entity => entity.EntityId == splineId),
                "redo removes target again");
            workspace.RestoreState(beforeDelete);

            var legacy = beforeDelete.Content with
            {
                SchemaVersion = ProjectSchema.CurrentVersion - 1,
                Entities = beforeDelete.Content.Entities.Select(entity => entity.EntityId != areaId
                    ? entity
                    : entity with
                    {
                        Geometry = entity.Geometry! with
                        {
                            Area = entity.Geometry.Area! with
                            {
                                Splines = [new(7, splineId)],
                            },
                        },
                    }).ToArray(),
            };
            var migrated = ProjectReferences.Migrate(legacy with { SchemaVersion = ProjectSchema.CurrentVersion });
            var migratedLink = migrated.Entities.Single(entity => entity.EntityId == areaId)
                .Geometry!.Area!.Splines.Single();
            Equal(null, migratedLink.EntityId, "migration removes legacy entity-ID authority");
            Equal(splineId, migratedLink.Reference!.EntityId, "migration preserves stable target ID");
            Equal(7, migratedLink.SourceIndex, "migration preserves native source index as provenance");

            var valid = workspace.CaptureState();
            Throws<InvalidDataException>(() => workspace.RestoreState(ReplaceReference(
                valid, areaId, reference with { EntityKind = ProjectEntityKind.Cuboid })));
            Throws<InvalidDataException>(() => workspace.RestoreState(ReplaceReference(
                valid, areaId, reference with { EntityId = EntityId.New() })));
            Throws<InvalidDataException>(() => workspace.RestoreState(valid with
            {
                Content = valid.Content with
                {
                    Entities = valid.Content.Entities.Select(entity => entity.EntityId != areaId
                        ? entity
                        : entity with
                        {
                            Geometry = entity.Geometry! with
                            {
                                Area = entity.Geometry.Area! with
                                {
                                    Splines = [new(7, Reference: reference), new(7, Reference: reference)],
                                },
                            },
                        }).ToArray(),
                },
            }));
            Throws<InvalidDataException>(() => workspace.RestoreState(valid with
            {
                Content = valid.Content with
                {
                    Entities = valid.Content.Entities.Select(entity => entity.EntityId != areaId
                        ? entity
                        : entity with
                        {
                            Geometry = entity.Geometry! with
                            {
                                Area = entity.Geometry.Area! with
                                {
                                    Splines = [new(7, Reference: reference), new(8, Reference: reference)],
                                },
                            },
                        }).ToArray(),
                },
            }));
            Equal(valid, workspace.CaptureState(), "invalid references preserve the last known-good state");

            await VerifyReferenceCommandAsync(root);
            VerifyLargeGraph();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProjectIndexedReference IndexedReference(
        int sourceValue,
        string fieldKey,
        ProjectEntityKind kind,
        EntityId entityId) => new(sourceValue, new(
        ProjectReferenceDomain.Entity,
        fieldKey,
        true,
        EntityKind: kind,
        EntityId: entityId));

    private static ForgeProjectState ReplaceReference(
        ForgeProjectState state,
        EntityId areaId,
        ProjectReference reference) => state with
    {
        Content = state.Content with
        {
            Entities = state.Content.Entities.Select(entity => entity.EntityId != areaId
                ? entity
                : entity with
                {
                    Geometry = entity.Geometry! with
                    {
                        Area = entity.Geometry.Area! with
                        {
                            Splines = [new(7, Reference: reference)],
                        },
                    },
                }).ToArray(),
        },
    };

    private static ProjectEntity Area(EntityId id, IReadOnlyList<ProjectGeometryLink> splines) => new(
        id,
        "Area",
        "areas",
        ProjectTransform.Identity,
        null,
        Geometry: new(Area: new(new(0, 0, 0, 1), 0, splines, [], [], [], [])));

    private static void VerifyLargeGraph()
    {
        const int count = 25_000;
        var targets = Enumerable.Range(0, count).Select(index => new ProjectEntity(
            EntityId.New(), $"Spline {index}", "splines", ProjectTransform.Identity, null,
            Geometry: new(Spline: new([])))).ToArray();
        var area = Area(EntityId.New(), targets.Select((target, index) => new ProjectGeometryLink(
            index,
            Reference: new(
                ProjectReferenceDomain.Entity,
                ProjectReferences.AreaSplines,
                Nullable: true,
                EntityKind: ProjectEntityKind.Spline,
                EntityId: target.EntityId))).ToArray());
        var content = new ForgeProjectContent(
            ProjectSchema.CurrentVersion, ProjectSchema.ContentDocumentType,
            targets.Append(area).ToArray(), [], [], []);
        var graph = ProjectReferenceGraph.Create(content);
        Equal(count, graph.Outgoing(area.EntityId).Count, "25k outgoing references are indexed");
        Equal(area.EntityId, graph.Incoming(targets[^1].EntityId).Single().OwnerEntityId,
            "25k graph supports direct inbound lookup");
    }

    private static async Task VerifyReferenceCommandAsync(string root)
    {
        var tieId = EntityId.New();
        var shrubId = EntityId.New();
        var collisionId = EntityId.New();
        var tie = new ProjectEntity(
            tieId, "Tie", "ties", ProjectTransform.Identity,
            new(AssetId.Parse(new string('b', AssetId.TextLength)), AssetKind.Tie));
        var shrub = new ProjectEntity(
            shrubId, "Shrub", "shrubs", ProjectTransform.Identity,
            new(AssetId.Parse(new string('c', AssetId.TextLength)), AssetKind.Shrub));
        var collision = new ProjectEntity(
            collisionId, "Collision", "collision", ProjectTransform.Identity,
            new(AssetId.Parse(new string('d', AssetId.TextLength)), AssetKind.Collision),
            new("UYA", 3, "level_wad/collision", 0),
            Collision: new(
                ProjectCollisionPieceKind.Solid, 0, 0, 1, 3, [new(0, 1)],
                [tieId, shrubId]));
        var workspace = await ForgeProjectWorkspace.CreateAsync(
            Path.Combine(root, "command-project"),
            "Reference command",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
            new("UYA", "NTSC-U", "1.00", 3, UyaIsoService.SupportedMd5),
            [tie, shrub, collision]);
        var moved = ProjectTransform.Identity with { Position = new(4, 5, 6) };
        workspace.UpdateTransform(tieId, moved);
        Equal(moved, workspace.Content.Entities.Single(entity => entity.EntityId == tieId).Transform,
            "linked translation moves the selected TIE");
        Equal(moved, workspace.Content.Entities.Single(entity => entity.EntityId == shrubId).Transform,
            "linked translation moves another connected instance");
        Equal(moved,
            workspace.Content.Entities.Single(entity => entity.EntityId == collisionId).Transform,
            "linked translation moves collision once");
        var rotated = moved with { Rotation = new(0, 0, MathF.Sin(MathF.PI / 4), MathF.Cos(MathF.PI / 4)) };
        workspace.UpdateTransform(tieId, rotated);
        Equal(true, workspace.Content.Entities.Where(entity =>
                entity.EntityId == tieId || entity.EntityId == shrubId || entity.EntityId == collisionId)
            .All(entity => Equivalent(entity.Transform, rotated)),
            "linked rotation rotates the connected component exactly once");
        Throws<ArgumentException>(() => workspace.UpdateTransform(
            tieId, rotated with { Scale = new(2, 2, 2) }));
        Equal(ProjectTransform.Identity.Scale,
            workspace.Content.Entities.Single(entity => entity.EntityId == collisionId).Transform.Scale,
            "linked scale rejection preserves collision transform");
        var largeTie = new ProjectTransform(
            new(12345.678f, -9876.543f, 4567.89f),
            new(0.10259784f, 0.20519568f, 0.30779353f, 0.9233805f),
            new(1.25f, 0.75f, 2));
        var largeShrub = new ProjectTransform(
            new(-23456.78f, 34567.89f, -5678.901f),
            new(-0.30618623f, 0.20412415f, 0.10206208f, 0.9237604f),
            new(0.5f, 1.5f, 0.9f));
        var largeCollision = ProjectTransform.Identity with { Position = new(15000, 12000, -8000) };
        workspace.RestoreState(workspace.CaptureState() with
        {
            Content = workspace.Content with
            {
                Entities = workspace.Content.Entities.Select(entity => entity.EntityId switch
                {
                    var id when id == tieId => entity with { Transform = largeTie },
                    var id when id == shrubId => entity with { Transform = largeShrub },
                    var id when id == collisionId => entity with { Transform = largeCollision },
                    _ => entity,
                }).ToArray(),
            },
        });
        var sharedRotation = System.Numerics.Matrix4x4.CreateTranslation(-10000, 20000, -30000)
            * System.Numerics.Matrix4x4.CreateFromAxisAngle(
                System.Numerics.Vector3.Normalize(new(1, 2, 3)), MathF.PI / 12)
            * System.Numerics.Matrix4x4.CreateTranslation(10000, -20000, 30000);
        workspace.UpdateTransforms([
            new(tieId, ApplyRigid(largeTie, sharedRotation)),
            new(shrubId, ApplyRigid(largeShrub, sharedRotation)),
            new(collisionId, ApplyRigid(largeCollision, sharedRotation)),
        ]);
        Equal(true, workspace.Content.Entities.Where(entity =>
                entity.EntityId == tieId || entity.EntityId == shrubId || entity.EntityId == collisionId)
            .All(entity => Equivalent(entity.Transform, ApplyRigid(entity.EntityId switch
            {
                var id when id == tieId => largeTie,
                var id when id == shrubId => largeShrub,
                _ => largeCollision,
            }, sharedRotation), 0.01f)),
            "linked rotation tolerates serialized large-coordinate precision");
        await using var runtime = new EditorRuntime(
            transformCapabilityResolver: entity => entity.Collision is null
                ? EditorTransformCapabilities.None
                : EditorTransformCapabilities.Translate | EditorTransformCapabilities.Rotate);
        _ = await runtime.OpenAsync(workspace.RootPath, TimeSpan.Zero);
        var snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"),
            EditorCommandKind.UpdateTransform,
            [tieId],
            Transform: moved));
        Equal(true, snapshot.Entities.Where(entity =>
                entity.EntityId == tieId || entity.EntityId == shrubId || entity.EntityId == collisionId)
            .All(entity => entity.Transform == moved),
            "runtime expands one linked translation across the connected component");
        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"),
            EditorCommandKind.UpdateTransform,
            [tieId],
            Transform: rotated));
        Equal(true, snapshot.Entities.Where(entity =>
                entity.EntityId == tieId || entity.EntityId == shrubId || entity.EntityId == collisionId)
            .All(entity => Equivalent(entity.Transform, rotated)),
            "runtime expands one linked rotation across the connected component");
        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
        Equal(true, snapshot.Entities.Where(entity =>
                entity.EntityId == tieId || entity.EntityId == shrubId || entity.EntityId == collisionId)
            .All(entity => entity.Transform == moved),
            "linked rotation is undoable as one command");
        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
        Equal(true, snapshot.Entities.Where(entity =>
                entity.EntityId == tieId || entity.EntityId == shrubId || entity.EntityId == collisionId)
            .All(entity => Equivalent(entity.Transform, rotated)),
            "linked rotation is redoable as one command");
        _ = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
        _ = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.DeleteEntities, [shrubId]));
        Equal(true, snapshot.Entities.Single(entity => entity.EntityId == collisionId)
            .Collision!.LinkedInstanceEntityIds!.SequenceEqual([tieId]),
            "deleting an instance clears only its semantic collision link");
        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
        Equal(true, snapshot.Entities.Single(entity => entity.EntityId == collisionId)
            .Collision!.LinkedInstanceEntityIds!.SequenceEqual([tieId, shrubId]),
            "collision link cleanup is undoable");
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static bool Equivalent(ProjectTransform left, ProjectTransform right, float positionTolerance = 0.0001f)
    {
        static bool Near(float first, float second) => MathF.Abs(first - second) <= 0.0001f;
        var dot = left.Rotation.X * right.Rotation.X + left.Rotation.Y * right.Rotation.Y
            + left.Rotation.Z * right.Rotation.Z + left.Rotation.W * right.Rotation.W;
        return MathF.Abs(left.Position.X - right.Position.X) <= positionTolerance
            && MathF.Abs(left.Position.Y - right.Position.Y) <= positionTolerance
            && MathF.Abs(left.Position.Z - right.Position.Z) <= positionTolerance
            && Near(MathF.Abs(dot), 1)
            && Near(left.Scale.X, right.Scale.X)
            && Near(left.Scale.Y, right.Scale.Y)
            && Near(left.Scale.Z, right.Scale.Z);
    }

    private static ProjectTransform ApplyRigid(ProjectTransform value, System.Numerics.Matrix4x4 delta)
    {
        var rotation = System.Numerics.Quaternion.Normalize(new(
            value.Rotation.X, value.Rotation.Y, value.Rotation.Z, value.Rotation.W));
        var matrix = System.Numerics.Matrix4x4.CreateScale(value.Scale.X, value.Scale.Y, value.Scale.Z)
            * System.Numerics.Matrix4x4.CreateFromQuaternion(rotation)
            * System.Numerics.Matrix4x4.CreateTranslation(value.Position.X, value.Position.Y, value.Position.Z)
            * delta;
        Equal(true, System.Numerics.Matrix4x4.Decompose(matrix, out _, out rotation, out var position),
            "test rigid transform decomposes");
        rotation = System.Numerics.Quaternion.Normalize(rotation);
        return new(
            new(position.X, position.Y, position.Z),
            new(rotation.X, rotation.Y, rotation.Z, rotation.W),
            value.Scale);
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}

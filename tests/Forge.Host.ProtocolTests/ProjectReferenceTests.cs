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
            var areaId = EntityId.New();
            var mobyId = EntityId.New();
            var mobyAssetId = AssetId.Parse(new string('a', AssetId.TextLength));
            var spline = new ProjectEntity(
                splineId, "Spline", "splines", ProjectTransform.Identity, null,
                Geometry: new(Spline: new([])));
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
            var workspace = await ForgeProjectWorkspace.CreateAsync(
                Path.Combine(root, "project"),
                "References",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 3, UyaIsoService.SupportedMd5),
                [spline, moby, area]);

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
            var afterDelete = workspace.CaptureState();
            history.Push(beforeDelete, afterDelete, [], [], [splineId], 256);
            Equal(0, workspace.Content.Entities.Single(entity => entity.EntityId == areaId)
                .Geometry!.Area!.Splines.Count, "delete clears nullable area link atomically");
            Equal(true, history.TryUndo(out var state, out _, out _), "reference-clearing delete is undoable");
            workspace.RestoreState(state);
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
                new(tieId, ProjectTransform.Identity)));
        var workspace = await ForgeProjectWorkspace.CreateAsync(
            Path.Combine(root, "command-project"),
            "Reference command",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
            new("UYA", "NTSC-U", "1.00", 3, UyaIsoService.SupportedMd5),
            [tie, shrub, collision]);
        await using var runtime = new EditorRuntime(
            transformCapabilityResolver: entity => entity.Collision is null
                ? EditorTransformCapabilities.None
                : EditorTransformCapabilities.Translate);
        _ = await runtime.OpenAsync(workspace.RootPath, TimeSpan.Zero);
        var snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"),
            EditorCommandKind.SetEntityReference,
            [collisionId],
            ReferenceUpdate: new(ProjectReferences.CollisionAttachment, null, shrubId)));
        Equal(shrubId, snapshot.References.Single(reference =>
            reference.Reference.FieldKey == ProjectReferences.CollisionAttachment).Reference.EntityId,
            "reference command updates the stable target");
        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.Undo, []));
        Equal(tieId, snapshot.References.Single(reference =>
            reference.Reference.FieldKey == ProjectReferences.CollisionAttachment).Reference.EntityId,
            "reference command is undoable");
        snapshot = await runtime.ExecuteAsync(new(
            Guid.NewGuid().ToString("D"), EditorCommandKind.Redo, []));
        Equal(shrubId, snapshot.References.Single(reference =>
            reference.Reference.FieldKey == ProjectReferences.CollisionAttachment).Reference.EntityId,
            "reference command is redoable");
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}

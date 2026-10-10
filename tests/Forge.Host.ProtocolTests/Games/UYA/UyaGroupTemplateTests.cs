using Forge.Host.Domain;
using Forge.Host.Games.UYA;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaGroupTemplateTests
{
    public static async Task RunAsync()
    {
        var catalog = UyaGroupTemplateDataset.Create();
        Equal(1, catalog.SchemaVersion, "UYA group-template dataset version");
        Equal([UyaGroupTemplateService.NodeTemplateId, UyaGroupTemplateService.BaseTemplateId],
            catalog.Templates.Select(value => value.TemplateId).Order(StringComparer.Ordinal).ToArray(),
            "UYA group-template dataset identities");
        var nodeRule = catalog.Templates.Single(value => value.TemplateId == UyaGroupTemplateService.NodeTemplateId);
        Equal("0x1A47", nodeRule.SeedClass, "node template seed OClass comes from the dataset");
        Equal(true, nodeRule.ReferenceRules.Any(rule => rule.TargetKinds.Contains(ProjectEntityKind.Cuboid)),
            "node template dataset contains referenced-cuboid traversal");
        var baseRule = catalog.Templates.Single(value => value.TemplateId == UyaGroupTemplateService.BaseTemplateId);
        Equal(true, baseRule.IncludeLinkedCollision
            && baseRule.NearbyRules.Any(rule => rule.EntityKind == ProjectEntityKind.Cuboid),
            "base template dataset contains collision and cuboid expansion");

        var root = Path.Combine(Path.GetTempPath(), $"forge-uya-group-templates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var fixture = CreateFixture();
            var projectPath = Path.Combine(root, "project");
            await ForgeProjectWorkspace.CreateAsync(
                projectPath,
                "UYA template goldens",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 40, UyaIsoService.SupportedMd5),
                fixture.Entities,
                null,
                null,
                null,
                fixture.PVars);

            await using var runtime = new EditorRuntime(
                groupTemplateEvaluator: UyaGroupTemplateService.EvaluateAsync,
                groupTemplates: UyaGroupTemplateService.Definitions);
            var snapshot = await runtime.OpenAsync(projectPath, TimeSpan.Zero);
            Equal([UyaGroupTemplateService.NodeTemplateId, UyaGroupTemplateService.BaseTemplateId],
                snapshot.GroupTemplates.Select(value => value.TemplateId).Order(StringComparer.Ordinal).ToArray(),
                "UYA template catalog is adapter-owned and deterministic");

            var red = await runtime.PreviewGroupTemplateAsync(Request(
                UyaGroupTemplateService.BaseTemplateId, fixture.RedBase));
            Equal("Red Siege Base", red.SuggestedName, "red base name follows Multiplayer Config evidence");
            Equal(Sorted(fixture.RedBase, fixture.RedComputer, fixture.RedAmmo,
                    fixture.RedNodeTurret, fixture.RedAntiVehicleTurret,
                    fixture.RedGatling, fixture.RedGatlingAttachment1, fixture.RedGatlingAttachment2,
                    fixture.RedGatlingAttachment3,
                    fixture.RedBaseTieNear, fixture.RedBaseTieFar, fixture.RedBaseCollision,
                    fixture.RedBaseCuboid), red.Members,
                "red base golden membership");
            Equal(false, red.Members.Contains(fixture.BlueBase), "red base excludes the blue base");
            Equal(false, red.Members.Contains(fixture.BlueBaseCuboid), "red base excludes the blue cuboid");
            Equal(false, red.Members.Contains(fixture.UnrelatedCuboid), "red base excludes distant cuboids");
            Equal(true, red.Candidates.All(value => value.Evidence.Count > 0),
                "every base candidate carries evidence");

            var blue = await runtime.PreviewGroupTemplateAsync(Request(
                UyaGroupTemplateService.BaseTemplateId, fixture.BlueBase));
            Equal("Blue Siege Base", blue.SuggestedName, "blue base name follows Multiplayer Config evidence");
            Equal(true, blue.Members.Contains(fixture.BlueBaseCuboid), "blue base includes its nearby cuboid");

            var node = await runtime.PreviewGroupTemplateAsync(Request(
                UyaGroupTemplateService.NodeTemplateId, fixture.NodeA));
            Equal(Sorted(fixture.NodeA, fixture.Attached, fixture.Parented, fixture.Defenses,
                    fixture.SpawnedDefense, fixture.NodePad, fixture.Hallway, fixture.NodeCuboid), node.Members,
                "node golden membership combines PVar and proximity evidence");
            var ambiguous = node.Candidates.Single(value => value.EntityId == fixture.AmbiguousHallway);
            Equal(EditorGroupTemplateCandidateDisposition.Ambiguous, ambiguous.Disposition,
                "equidistant hallway remains excluded and ambiguous");
            Equal(true, ambiguous.Evidence.Count > 1, "ambiguous hallway explains both distances");
            Equal(false, node.Members.Contains(fixture.AmbiguousHallway),
                "ambiguous candidates do not enter ordinary membership");
            Equal(false, node.Members.Contains(fixture.NearbyNodeCuboid),
                "nearby cuboids without a typed reference remain excluded");

            snapshot = await runtime.ApplyGroupTemplatePreviewAsync(CommandId(), node.Token);
            var group = snapshot.Groups.Single();
            Equal(fixture.NodeA, group.Template!.SeedEntityId, "accepted template persists its stable seed");
            snapshot = await runtime.ExecuteAsync(new(
                CommandId(), EditorCommandKind.AddGroupMembers, [fixture.Manual], GroupEdit: new(group.GroupId)));
            var tighter = await runtime.PreviewGroupTemplateAsync(Request(
                UyaGroupTemplateService.NodeTemplateId,
                fixture.NodeA,
                group.GroupId,
                tieRadius: 8));
            Equal([fixture.Hallway], tighter.RemovedMembers, "threshold changes preview removals before mutation");
            Equal([fixture.Manual], tighter.ManualAdditions, "regeneration discloses manual additions");
            Equal(true, snapshot.Groups.Single().Members.Contains(fixture.Hallway),
                "preview does not mutate accepted membership");
            snapshot = await runtime.ApplyGroupTemplatePreviewAsync(CommandId(), tighter.Token);
            Equal(true, snapshot.Groups.Single().Members.Contains(fixture.Manual),
                "confirmed regeneration preserves manual additions");
            Equal(false, snapshot.Groups.Single().Members.Contains(fixture.Hallway),
                "confirmed regeneration applies the threshold diff");
            await runtime.SaveAsync();
            var reopened = await ForgeProjectWorkspace.OpenAsync(projectPath);
            Equal(fixture.NodeA, reopened.Content.Groups.Single().Template!.SeedEntityId,
                "UYA template seed survives save and reopen");

            var automaticPath = Path.Combine(root, "automatic-project");
            var automatic = await ForgeProjectWorkspace.CreateAsync(
                automaticPath,
                "Automatic UYA template groups",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 40, UyaIsoService.SupportedMd5),
                fixture.Entities,
                null,
                null,
                null,
                fixture.PVars);
            Equal(4, await UyaGroupTemplateService.ApplyDefaultsAsync(automatic),
                "automatic generation creates one group per matching seed");
            Equal(0, await UyaGroupTemplateService.ApplyDefaultsAsync(automatic),
                "automatic generation is idempotent");
            Equal(4, automatic.Content.Groups.Count, "automatic groups remain unique");
            Equal(4, automatic.Content.Groups.Select(group => group.Template!.SeedEntityId).Distinct().Count(),
                "automatic groups retain distinct seeds");
            await automatic.SaveAsync();
            var automaticReopened = await ForgeProjectWorkspace.OpenAsync(automaticPath);
            Equal(4, automaticReopened.Content.Groups.Count,
                "automatic groups survive project persistence");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static EditorGroupTemplateRequest Request(
        string templateId,
        EntityId seed,
        GroupId? groupId = null,
        double? tieRadius = null)
    {
        var definition = UyaGroupTemplateService.Definitions.Single(value => value.TemplateId == templateId);
        return new(
            templateId,
            definition.TemplateVersion,
            definition.Parameters.Select(parameter => new ProjectGroupTemplateParameter(
                parameter.Key,
                parameter.Key == "tie-radius" && tieRadius is { } radius ? radius : parameter.DefaultValue)).ToArray(),
            groupId,
            seed);
    }

    private static Fixture CreateFixture()
    {
        var sourceIndex = 0;
        var redBase = Moby("Red Base Light", 0x0CBE, sourceIndex++, 0, 0);
        var blueBase = Moby("Blue Base Light", 0x0CBE, sourceIndex++, 100, 0);
        var config = Moby("Multiplayer Config", 0x106A, sourceIndex++, 50, 0);
        var redComputer = Moby("Red Base Computer", 0x1C57, sourceIndex++, 2, 0);
        var redAmmo = Moby("Red Ammo Pad", 0x1388, sourceIndex++, 4, 0);
        var redNodeTurret = Moby("Red Node Turret", 0x1A63, sourceIndex++, 5, 0);
        var redAntiVehicleTurret = Moby("Red Anti-vehicle Missile Turret", 0x1BBD, sourceIndex++, 5, 0);
        var redGatling = Moby("Red Gattling Turret", 0x1AD6, sourceIndex++, 6, 0);
        var redGatlingAttachment1 = Moby("Gatling attachment 1", 0x1D50, sourceIndex++, 6, 0);
        var redGatlingAttachment2 = Moby("Gatling attachment 2", 0x1D51, sourceIndex++, 6, 0);
        var redGatlingAttachment3 = Moby("Gatling attachment 3", 0x1D52, sourceIndex++, 6, 0);
        var nodeA = Moby("Siege Node A", 0x1A47, sourceIndex++, 0, 100);
        var nodeB = Moby("Siege Node B", 0x1A47, sourceIndex++, 40, 100);
        var attached = Moby("Node attachment", 0x1ABE, sourceIndex++, 3, 100);
        var parented = Moby("Node Turret", 0x1A63, sourceIndex++, 4, 100);
        var defenses = Moby("Mobile Defenses", 0x1A97, sourceIndex++, 5, 100);
        var spawnedDefense = Moby("Spawned defense", 0x1ABE, sourceIndex++, 6, 100);
        var manual = Moby("Manual addition", 0x1F5E, sourceIndex++, 80, 100);
        var redBaseTieNear = Tie("Red base center", 0x02D5, sourceIndex++, 1, 0);
        var redBaseTieFar = Tie("Red base ring", 0x1D42, sourceIndex++, 15, 0);
        var redBaseCollision = Collision("Red base collision", sourceIndex++, redBaseTieNear.EntityId);
        var redBaseCuboid = Cuboid("Red base cuboid", sourceIndex++, 3, 0);
        var blueBaseCuboid = Cuboid("Blue base cuboid", sourceIndex++, 103, 0);
        var unrelatedCuboid = Cuboid("Unrelated cuboid", sourceIndex++, 25, 0);
        var blueBaseTie = Tie("Blue base center", 0x02D6, sourceIndex++, 101, 0);
        var unrelatedTie = Tie("Unrelated TIE", 0x0200, sourceIndex++, 2, 0);
        var nodePad = Tie("Node pad", 0x1F1F, sourceIndex++, 0, 100);
        var hallway = Tie("Node hallway", 0x1F19, sourceIndex++, 10, 100);
        var ambiguousHallway = Tie("Shared hallway", 0x1F19, sourceIndex++, 20, 100);
        var nodeCuboid = Cuboid("Referenced node cuboid", sourceIndex++, 1_000, 1_000);
        var nearbyNodeCuboid = Cuboid("Unreferenced nearby cuboid", sourceIndex++, 2, 100);
        var entities = new[]
        {
            redBase, blueBase, config, redComputer, redAmmo, redNodeTurret, redAntiVehicleTurret,
            redGatling, redGatlingAttachment1, redGatlingAttachment2, redGatlingAttachment3,
            nodeA, nodeB, attached, parented, defenses,
            spawnedDefense, manual, redBaseTieNear, redBaseTieFar, redBaseCollision, blueBaseTie,
            redBaseCuboid, blueBaseCuboid, unrelatedCuboid,
            unrelatedTie, nodePad, hallway, ambiguousHallway, nodeCuboid, nearbyNodeCuboid,
        };
        var entries = new[]
        {
            PVar(config, ("redBaseLight", redBase), ("blueBaseLight", blueBase)),
            PVar(redBase, ("baseComputer", redComputer)),
            PVar(redComputer, ("baseLight", redBase)),
            PVar(redAmmo, ("baseLight", redBase)),
            PVar(redNodeTurret, ("parentNode", redBase)),
            PVar(redAntiVehicleTurret, ("parentNode", redBase)),
            PVar(redGatling, ("baseLight", redBase),
                ("attachedMoby1", redGatlingAttachment1),
                ("attachedMoby2", redGatlingAttachment2),
                ("attachedMoby3", redGatlingAttachment3)),
            PVar(nodeA, ("attachedMoby1", attached), ("attachedDefenses", defenses)),
            PVar(attached, ("cameraCuboid", nodeCuboid)),
            PVar(parented, ("parentNode", nodeA)),
            PVar(defenses, ("spawnMoby1", spawnedDefense)),
        };
        return new(
            entities,
            new(ProjectMobyPVarSchema.CurrentVersion, entries.Length, entries),
            redBase.EntityId,
            blueBase.EntityId,
            redComputer.EntityId,
            redAmmo.EntityId,
            redNodeTurret.EntityId,
            redAntiVehicleTurret.EntityId,
            redGatling.EntityId,
            redGatlingAttachment1.EntityId,
            redGatlingAttachment2.EntityId,
            redGatlingAttachment3.EntityId,
            redBaseTieNear.EntityId,
            redBaseTieFar.EntityId,
            redBaseCollision.EntityId,
            redBaseCuboid.EntityId,
            blueBaseCuboid.EntityId,
            unrelatedCuboid.EntityId,
            nodeA.EntityId,
            attached.EntityId,
            parented.EntityId,
            defenses.EntityId,
            spawnedDefense.EntityId,
            nodePad.EntityId,
            hallway.EntityId,
            ambiguousHallway.EntityId,
            nodeCuboid.EntityId,
            nearbyNodeCuboid.EntityId,
            manual.EntityId);
    }

    private static ProjectMobyPVar PVar(
        ProjectEntity owner,
        params (string Field, ProjectEntity Target)[] values)
    {
        var bytes = new byte[Math.Max(4, values.Length * 4)];
        var references = values.Select((value, index) => new ProjectMobyPVarReference(
            index * 4,
            4,
            -1,
            value.Target.Provenance!.SourceIndex,
            new(ProjectReferenceDomain.Entity, value.Field, true,
                EntityKind: ProjectReferences.KindOf(value.Target), EntityId: value.Target.EntityId))).ToArray();
        return new(
            owner.EntityId,
            null,
            bytes,
            bytes.ToArray(),
            references.Select(value => value.Offset).ToArray(),
            [],
            references);
    }

    private static ProjectEntity Moby(string name, int oClass, int sourceIndex, float x, float y) => new(
        EntityId.New(),
        name,
        "mobys",
        ProjectTransform.Identity with { Position = new(x, y, 0) },
        null,
        new("UYA", 40, "gameplay/core/moby_instances", sourceIndex),
        Source: new(oClass, [1], SourceIndex: sourceIndex));

    private static ProjectEntity Tie(string name, int oClass, int sourceIndex, float x, float y) => new(
        EntityId.New(),
        name,
        "ties",
        ProjectTransform.Identity with { Position = new(x, y, 0) },
        null,
        new("UYA", 40, "gameplay/core/tie_instances", sourceIndex),
        Source: new(oClass, [1], SourceIndex: sourceIndex));

    private static ProjectEntity Collision(string name, int sourceIndex, EntityId linkedInstance) => new(
        EntityId.New(),
        name,
        "collision",
        ProjectTransform.Identity,
        new(AssetId.Parse(new string('a', AssetId.TextLength)), AssetKind.Collision),
        new("UYA", 40, "collision/primary", sourceIndex),
        Collision: new(ProjectCollisionPieceKind.Solid, 0, sourceIndex, 1, 3, [new(0, 1)],
            LinkedInstanceEntityIds: [linkedInstance]));

    private static ProjectEntity Cuboid(string name, int sourceIndex, float x, float y) => new(
        EntityId.New(),
        name,
        "cuboids",
        ProjectTransform.Identity with { Position = new(x, y, 0) },
        null,
        new("UYA", 40, "gameplay/core/cuboids", sourceIndex),
        Geometry: new(Cuboid: new(
            [1f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, 0, 1],
            [1f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0],
            new(0, 0, 0))));

    private static string CommandId() => Guid.NewGuid().ToString("D");

    private static EntityId[] Sorted(params EntityId[] values) => values
        .OrderBy(value => value.ToString(), StringComparer.Ordinal).ToArray();

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (expected is System.Collections.IEnumerable expectedItems
            && actual is System.Collections.IEnumerable actualItems
            && expected is not string && actual is not string)
        {
            if (expectedItems.Cast<object?>().SequenceEqual(actualItems.Cast<object?>())) return;
        }
        else if (EqualityComparer<T>.Default.Equals(expected, actual)) return;
        throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    private sealed record Fixture(
        IReadOnlyList<ProjectEntity> Entities,
        ProjectMobyPVarState PVars,
        EntityId RedBase,
        EntityId BlueBase,
        EntityId RedComputer,
        EntityId RedAmmo,
        EntityId RedNodeTurret,
        EntityId RedAntiVehicleTurret,
        EntityId RedGatling,
        EntityId RedGatlingAttachment1,
        EntityId RedGatlingAttachment2,
        EntityId RedGatlingAttachment3,
        EntityId RedBaseTieNear,
        EntityId RedBaseTieFar,
        EntityId RedBaseCollision,
        EntityId RedBaseCuboid,
        EntityId BlueBaseCuboid,
        EntityId UnrelatedCuboid,
        EntityId NodeA,
        EntityId Attached,
        EntityId Parented,
        EntityId Defenses,
        EntityId SpawnedDefense,
        EntityId NodePad,
        EntityId Hallway,
        EntityId AmbiguousHallway,
        EntityId NodeCuboid,
        EntityId NearbyNodeCuboid,
        EntityId Manual);
}

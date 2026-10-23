using Forge.Host.Bridge;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using System.Security.Cryptography;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaContentImportCapabilityTests
{
    public static async Task RunAsync()
    {
        var baseline = UyaContentImportCapabilities.Create("NTSC-U", "1.00");
        Equal(ContentImportCapabilitySchema.CurrentVersion, baseline.SchemaVersion, "matrix schema version");
        Equal("UYA", baseline.Game, "matrix game");
        Equal(18, baseline.Families.Count, "complete family inventory");
        Equal(baseline.Families.Count, baseline.Families.Select(value => value.Family).Distinct().Count(),
            "unique family IDs");
        Equal(new[] { "ties", "shrubs", "tfrags" }, baseline.Families
            .Where(value => value.DefaultSelectedWhenReady).Select(value => value.Family).ToArray(),
            "core default families");
        Equal(true, baseline.Families.All(value => !value.Selectable && !value.DefaultSelected),
            "unknown donor inventory does not enable import");
        Equal(true, Family(baseline, "tfrags").ImplementationReady,
            "dependency-complete tfrag import is implementation-ready");
        Equal(true, Family(baseline, "collision").ImplementationReady,
            "dependency-complete collision import is implementation-ready");
        Equal(true, Family(baseline, "cuboids").ImplementationReady,
            "dependency-complete cuboid import is implementation-ready");
        Equal(true, Family(baseline, "spheres").ImplementationReady,
            "dependency-complete sphere import is implementation-ready");
        Equal(true, Family(baseline, "splines").ImplementationReady,
            "dependency-complete spline import is implementation-ready");
        Equal(true, Family(baseline, "paths").ImplementationReady,
            "dependency-complete grind-path import is implementation-ready");
        Equal(true, Family(baseline, "areas").ImplementationReady,
            "dependency-complete area import is implementation-ready");
        Equal(true, Family(baseline, "mobys").ImplementationReady,
            "dependency-complete Moby import is implementation-ready");
        Equal(true, Family(baseline, "skybox").ImplementationReady,
            "replace-only skybox import is implementation-ready");
        Equal(ContentImportMode.Replace, Family(baseline, "skybox").Mode, "skybox replace-only mode");
        Equal(true, Family(baseline, "levelSettings").ImplementationReady,
            "replace-only level-settings import is implementation-ready");
        Equal(ContentImportMode.Replace, Family(baseline, "levelSettings").Mode,
            "level settings replace-only mode");
        Equal(ContentImportMode.Replace, Family(baseline, "directionalLights").Mode,
            "directional lights replace-only mode");
        Equal(true, Family(baseline, "directionalLights").ImplementationReady,
            "replace-only directional-light import is implementation-ready");
        Equal(ContentImportMode.Replace, Family(baseline, "hud").Mode, "HUD replace-only mode");
        Equal(true, Family(baseline, "hud").ImplementationReady,
            "replace-only HUD import is implementation-ready");
        Equal(ContentImportMode.Replace, Family(baseline, "fx").Mode, "FX replace-only mode");
        Equal(true, Family(baseline, "fx").ImplementationReady,
            "replace-only FX import is implementation-ready");
        Equal(true, Family(baseline, "mapGroups").ImplementationReady,
            "replace-only map-group import is implementation-ready");
        Equal(ContentImportMode.Replace, Family(baseline, "mapGroups").Mode,
            "map groups are replace-only");
        Equal(true, Family(baseline, "occlusion").ImplementationReady,
            "replace-only occlusion import is implementation-ready");
        Equal(ContentImportMode.Replace, Family(baseline, "occlusion").Mode,
            "occlusion is replace-only");
        Equal(new[] { "opaqueContent" }, baseline.Families
            .Where(value => value.Mode == ContentImportMode.Unsupported)
            .Select(value => value.Family).ToArray(), "scope-disabled families");
        Equal(true, baseline.Families.Where(value => value.Mode != ContentImportMode.Unsupported).All(value =>
                value.Ownership.Length > 0
                && value.Dependencies.Count > 0
                && value.ConflictNamespace.Length > 0
                && value.NativeSemantics.Length > 0
                && value.ProtectedSurvivorPolicy.Length > 0
                && value.NoEditIdentity.Length > 0
                && value.Limits.Count > 0
                && value.Evidence.Count > 0),
            "supported-mode contracts include ownership and dependency evidence");

        var evaluated = UyaContentImportCapabilities.Create("NTSC-U", "1.00",
        [
            new("ties", ContentImportSourceState.Present),
            new("shrubs", ContentImportSourceState.Absent),
            new("tfrags", ContentImportSourceState.Empty),
            new("mobys", ContentImportSourceState.Malformed, "moby table is truncated"),
            new("collision", ContentImportSourceState.Present),
            new("cuboids", ContentImportSourceState.Present),
            new("spheres", ContentImportSourceState.Present),
            new("splines", ContentImportSourceState.Present),
            new("paths", ContentImportSourceState.Present),
            new("areas", ContentImportSourceState.Present),
            new("skybox", ContentImportSourceState.Present),
            new("levelSettings", ContentImportSourceState.Present),
            new("directionalLights", ContentImportSourceState.Present),
            new("hud", ContentImportSourceState.Present),
            new("fx", ContentImportSourceState.Present),
            new("mapGroups", ContentImportSourceState.Present),
            new("occlusion", ContentImportSourceState.Present),
        ]);
        Equal(ContentImportSourceState.Present, Family(evaluated, "ties").SourceState,
            "present family state");
        Equal(true, Family(evaluated, "ties").Selectable,
            "implemented present TIE family is selectable");
        Equal(true, Family(evaluated, "ties").DefaultSelected,
            "implemented present TIE family is selected by default");
        Equal("Shrubs are absent from the donor map.", Family(evaluated, "shrubs").DisabledReason,
            "absent family reason");
        Equal("Tfrags are empty in the donor map.", Family(evaluated, "tfrags").DisabledReason,
            "empty family reason");
        Equal("moby table is truncated", Family(evaluated, "mobys").DisabledReason,
            "malformed family reason");
        Equal(true, Family(evaluated, "collision").Selectable,
            "present collision family is selectable");
        Equal(true, Family(evaluated, "cuboids").Selectable && Family(evaluated, "spheres").Selectable,
            "present shape families are selectable");
        Equal(true, Family(evaluated, "splines").Selectable && Family(evaluated, "paths").Selectable,
            "present spline and grind-path families are selectable");
        Equal(true, Family(evaluated, "areas").Selectable,
            "present area family is selectable");
        Equal(true, Family(evaluated, "skybox").Selectable,
            "present skybox is selectable for replacement");
        Equal(true, Family(evaluated, "levelSettings").Selectable,
            "present level settings are selectable for replacement");
        Equal(true, Family(evaluated, "directionalLights").Selectable,
            "present directional lights are selectable for replacement");
        Equal(true, Family(evaluated, "hud").Selectable,
            "present HUD is selectable for replacement");
        Equal(true, Family(evaluated, "fx").Selectable,
            "present FX bank is selectable for replacement");
        Equal(true, Family(evaluated, "mapGroups").Selectable,
            "present map groups are selectable for replacement");
        Equal(true, Family(evaluated, "occlusion").Selectable,
            "present occlusion is selectable for replacement");
        var safeMobys = UyaContentImportCapabilities.Create("NTSC-U", "1.00",
            [new("mobys", ContentImportSourceState.Present)]);
        Equal(true, Family(safeMobys, "mobys").Selectable,
            "safe present Moby family is selectable");
        Throws<ArgumentException>(() => UyaContentImportCapabilities.Create("NTSC-U", "1.00",
            [new("mobys", ContentImportSourceState.Malformed)]),
            "malformed inventory requires a diagnostic");
        Throws<ArgumentException>(() => UyaContentImportCapabilities.Create("PAL", "1.00"),
            "unsupported target is rejected");

        var request = new ContentImportCapabilityRequestPayload(
            "UYA", "NTSC-U", "1.00",
            [new("ties", (uint)ContentImportSourceState.Present)]);
        var requestBytes = BridgePayloadCodec.EncodeContentImportCapabilityRequest(request);
        var decodedRequest = BridgePayloadCodec.DecodeContentImportCapabilityRequest(requestBytes);
        Equal(request.TargetGame, decodedRequest.TargetGame, "capability request game");
        Equal(request.Inventory.ToArray(), decodedRequest.Inventory.ToArray(), "capability request inventory");
        Equal(requestBytes, BridgePayloadCodec.EncodeContentImportCapabilityRequest(decodedRequest),
            "capability request bridge bytes");
        var response = await ProjectBridgeHandlers.HandleAsync(
            new(
                BridgeMessageKind.Request,
                BridgeOpcode.GetContentImportCapabilities,
                BridgeErrorCode.None,
                1,
                requestBytes),
            "test-sdk",
            _ => ValueTask.CompletedTask,
            CancellationToken.None);
        var decoded = BridgePayloadCodec.DecodeContentImportCapabilityMatrix(response);
        Equal((uint)ContentImportCapabilitySchema.CurrentVersion, decoded.SchemaVersion,
            "bridge matrix schema version");
        Equal((uint)ContentImportMode.AddAndReplace,
            decoded.Families.Single(value => value.Family == "ties").Mode,
            "bridge preserves planned mode");
        Equal(true, decoded.Families.Single(value => value.Family == "ties").Selectable,
            "bridge enables implemented TIE family");
        Equal(response, BridgePayloadCodec.EncodeContentImportCapabilityMatrix(decoded),
            "bridge matrix bytes are deterministic");
        Equal("6a41d0c192f263e2d8281e7eefc9c07c522927fe4022af8ff9f037b1da4f0c16",
            Convert.ToHexString(SHA256.HashData(response)).ToLowerInvariant(),
            "adapter capability snapshot hash");

        var applyRequest = new ContentImportApplyRequestPayload(
            "/project", "/assets", "/source.iso", new string('a', 32), 40,
            "import-11111111111111111111111111111111",
            [new("ties", (uint)ContentImportMode.Add)],
            [new(4, "trigger.cuboid", 7, (uint)UyaMobyExternalReferenceAction.Clear, null)]);
        var applyBytes = BridgePayloadCodec.EncodeContentImportApplyRequest(applyRequest);
        var decodedApply = BridgePayloadCodec.DecodeContentImportApplyRequest(applyBytes);
        Equal(applyRequest.BatchId, decodedApply.BatchId, "apply request batch ID");
        Equal(applyRequest.Selections.ToArray(), decodedApply.Selections.ToArray(),
            "apply request selections");
        Equal(applyRequest.ReferenceChoices.ToArray(), decodedApply.ReferenceChoices.ToArray(),
            "apply request reference choices");
        Equal(applyBytes, BridgePayloadCodec.EncodeContentImportApplyRequest(decodedApply),
            "apply request bridge bytes");

        var removalRequest = new ContentImportRemovalApplyRequestPayload(
            "/project", applyRequest.BatchId, new string('d', 64));
        var removalRequestBytes = BridgePayloadCodec.EncodeContentImportRemovalApplyRequest(removalRequest);
        Equal(removalRequest, BridgePayloadCodec.DecodeContentImportRemovalApplyRequest(removalRequestBytes),
            "removal apply request round trip");
        var removalPlan = new ContentImportRemovalPlanPayload(
            ContentImportRemovalSchema.CurrentVersion,
            applyRequest.BatchId,
            new string('d', 64),
            ["entity:11111111-1111-1111-1111-111111111111"],
            [new("asset:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Still referenced.")],
            [],
            [],
            ["Replace content is retained."]);
        var removalPlanBytes = BridgePayloadCodec.EncodeContentImportRemovalPlan(removalPlan);
        var decodedRemovalPlan = BridgePayloadCodec.DecodeContentImportRemovalPlan(removalPlanBytes);
        Equal(removalPlan.BatchId, decodedRemovalPlan.BatchId, "removal plan batch ID");
        Equal(removalPlanBytes, BridgePayloadCodec.EncodeContentImportRemovalPlan(decodedRemovalPlan),
            "removal plan bridge bytes");

        await VerifyReadOnlyCatalogAsync();
        VerifyPlanning();
    }

    private static async Task VerifyReadOnlyCatalogAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-import-readonly-{Guid.NewGuid():N}");
        _ = await AssetCatalogStore.OpenReadOnlyAsync(root);
        Equal(false, Directory.Exists(root), "read-only donor catalog open does not create directories");
    }

    private static void VerifyPlanning()
    {
        Throws<InvalidDataException>(() => UyaContentImportPlanningService.ValidateSourceIdentity(
                new(false, "UYA", "PAL", "1.00", "test", 0, new string('a', 32), "unsupported"),
                new string('a', 32), new string('a', 32)),
            "incompatible donor fails before inventory");
        var assetA = AssetId.Compute(AssetKind.Tie, 1, [1]);
        var assetB = AssetId.Compute(AssetKind.Tie, 1, [2]);
        var cuboidId = EntityId.New();
        var areaId = EntityId.New();
        var tieId = EntityId.New();
        var collisionTieId = EntityId.New();
        var collisionId = EntityId.New();
        var mobyAId = EntityId.New();
        var mobyBId = EntityId.New();
        var skyId = EntityId.New();
        var cameraCuboidId = EntityId.New();
        var cuboid = new ProjectEntity(
            cuboidId, "Cuboid", "cuboids", ProjectTransform.Identity, null,
            new("UYA", 41, "gameplay/core/cuboids", 0),
            Geometry: new(Cuboid: new([], [], new(0, 0, 0))));
        var area = new ProjectEntity(
            areaId, "Area", "areas", ProjectTransform.Identity, null,
            new("UYA", 41, "gameplay/core/areas", 0),
            Geometry: new(Area: new(new(0, 0, 0, 1), 0, [],
                [new(0, Reference: new(ProjectReferenceDomain.Entity, "test.cycle", true,
                    EntityKind: ProjectEntityKind.Cuboid, EntityId: cuboidId))], [], [], [])));
        var tie = new ProjectEntity(
            tieId, "Tie", "ties", ProjectTransform.Identity, new(assetA, AssetKind.Tie),
            new("UYA", 41, "gameplay/core/tie_instances", 0),
            Source: new(1, [1]));
        var collisionTie = tie with
        {
            EntityId = collisionTieId,
            Name = "Collision-linked TIE",
            Provenance = tie.Provenance! with { SourceIndex = 1 },
        };
        var collision = new ProjectEntity(
            collisionId, "Attached collision", "collision", ProjectTransform.Identity,
            new(AssetId.Compute(AssetKind.Collision, 1, [3]), AssetKind.Collision),
            new("UYA", 41, "collision/primary/solid", 0),
            Collision: new(ProjectCollisionPieceKind.Solid, 0, 0, 1, 3,
                [new(0x31, 1)], [collisionTieId]));
        var mobyA = new ProjectEntity(mobyAId, "Moby A", "mobys", ProjectTransform.Identity, null,
            new("UYA", 41, "gameplay/core/moby_instances", 0), Source: new(10, [1]));
        var mobyB = new ProjectEntity(mobyBId, "Moby B", "mobys", ProjectTransform.Identity, null,
            new("UYA", 41, "gameplay/core/moby_instances", 1), Source: new(11, [2]));
        var sky = new ProjectEntity(
            skyId, "Sky shell", "sky", ProjectTransform.Identity,
            new(AssetId.Compute(AssetKind.Sky, 1, [4]), AssetKind.Sky),
            new("UYA", 41, "level_wad/assets/sky", 0),
            SkyShell: new(0, 0, new(0, 0, 0), new(0, 0, 0)));
        var cameraCuboid = cuboid with
        {
            EntityId = cameraCuboidId,
            Name = "Camera cuboid",
            Provenance = cuboid.Provenance! with { SourceIndex = 1 },
        };
        var levelSettings = new ProjectLevelSettings(
            new(0, 0, 0), new(0, 0, 0), 0, 0, 0, 0,
            new(0, false, new(0, 0, 0), new(0, 0, 0), 0,
                LevelReference(-1, ProjectReferences.LevelShipPath, ProjectEntityKind.Spline, null),
                LevelReference(1, ProjectReferences.LevelShipCameraCuboidStart,
                    ProjectEntityKind.Cuboid, cameraCuboidId),
                LevelReference(1, ProjectReferences.LevelShipCameraCuboidEnd,
                    ProjectEntityKind.Cuboid, cameraCuboidId)));
        var pvars = new ProjectMobyPVarState(ProjectMobyPVarSchema.CurrentVersion, 2,
        [
            new(mobyAId, 0, [], [], [], [], [new(0, 4, -1, 1,
                new(ProjectReferenceDomain.Entity, "moby.peer", true, EntityKind: ProjectEntityKind.Moby,
                    EntityId: mobyBId))]),
            new(mobyBId, 1, [], [], [], [], [new(0, 4, -1, 0,
                new(ProjectReferenceDomain.Entity, "moby.peer", true, EntityKind: ProjectEntityKind.Moby,
                    EntityId: mobyAId))]),
        ]);
        var target = new ForgeProjectContent(0, "test",
            [tie, collisionTie, collision, cuboid, area, mobyA, mobyB, sky, cameraCuboid], [], [], [],
            LevelSettings: levelSettings, MobyPVars: pvars)
        {
            Groups = [new(GroupId.New(), "Protected import group", [tieId, skyId])],
        };
        var sources = Families(new Dictionary<string, UyaContentImportSourceFamily>(StringComparer.Ordinal)
        {
            ["ties"] = new("ties", ContentImportSourceState.Present,
            [
                new("source:ties:gameplay/core/tie_instances:0", assetB.ToString(), 1),
                new("source:ties:gameplay/core/tie_instances:1", assetA.ToString(), 2),
            ], 320),
            ["cuboids"] = new("cuboids", ContentImportSourceState.Present,
                [new("source:cuboids:gameplay/core/cuboids:0")], 128),
            ["skybox"] = new("skybox", ContentImportSourceState.Present,
                [new("source:skybox:level_wad/assets/sky:0")], 80),
            ["levelSettings"] = new("levelSettings", ContentImportSourceState.Present,
                [new("source:levelSettings:singleton")], 132),
        });
        var input = new UyaContentImportPlanningInput(
            new string('a', 32), new string('b', 64), 40,
            "11111111-1111-1111-1111-111111111111", new string('c', 64), 41,
            sources, target,
            [new("ties", ContentImportMode.Add), new("cuboids", ContentImportMode.Replace)]);
        var plan = UyaContentImportPlanningService.CreatePlan(input);
        var repeated = UyaContentImportPlanningService.CreatePlan(input with
        {
            Selections = input.Selections.Reverse().ToArray(),
        });
        Equal(plan.BatchId, repeated.BatchId, "selection order does not change stable batch ID");
        Equal(40, plan.SourceLevel, "level40 donor fixture");
        Equal(41, plan.TargetLevel, "level41 target fixture");
        Equal(448L, plan.EstimatedBytes, "selected family byte estimate");
        var tiePlan = PlanFamily(plan, "ties");
        Equal(2, tiePlan.Add.Count, "tie add set");
        Equal(new[] { $"asset:{assetA}" }, tiePlan.Reuse.ToArray(), "tie asset reuse set");
        Equal(new[] { "class:0x0001" }, tiePlan.Conflicts.ToArray(), "tie conflict set");
        Equal(new[] { "class:0x0001->allocate:custom" }, tiePlan.Remap.ToArray(), "tie remap set");
        Equal(Array.Empty<string>(), tiePlan.Blockers.ToArray(), "implemented TIE family is unblocked");
        var cuboidPlan = PlanFamily(plan, "cuboids");
        Equal(Array.Empty<string>(), cuboidPlan.Remove.ToArray(), "referenced cuboid is not removed");
        Equal(new[] { $"entity:{cuboidId}", $"entity:{cameraCuboidId}" }.Order(StringComparer.Ordinal).ToArray(),
            cuboidPlan.Protect.ToArray(), "outside references protect cuboids");
        Equal(Array.Empty<string>(), plan.Blockers.ToArray(),
            "dependency-complete selected families are unblocked");

        var tfragFamilies = Families(new Dictionary<string, UyaContentImportSourceFamily>(StringComparer.Ordinal)
        {
            ["tfrags"] = new("tfrags", ContentImportSourceState.Present,
            [
                new("source:tfrags:primary:0", ItemCount: 2),
                new("source:tfrags:chunk:1", ItemCount: 2),
                new("source:tfrags:chunk:2", ItemCount: 2),
            ], 1024),
        });
        var tfragPlan = UyaContentImportPlanningService.CreatePlan(input with
        {
            SourceFamilies = tfragFamilies,
            Target = target with
            {
                Tfrags = new(ProjectTfragSchema.CurrentVersion,
                    [new("level_wad/assets/asset_wad.bin", ProjectTfragSourceKind.Primary, 0, 0)]),
            },
            Selections = [new("tfrags", ContentImportMode.Add)],
        });
        Equal(Array.Empty<string>(), PlanFamily(tfragPlan, "tfrags").Blockers.ToArray(),
            "donor chunks do not require matching target chunk owners");
        Equal(true, PlanFamily(tfragPlan, "tfrags").Warnings.Any(value =>
                value.Contains("2 donor streamed tfrag owner(s)", StringComparison.Ordinal)),
            "plan reports donor chunks that will move into primary terrain");

        var tieReplace = UyaContentImportPlanningService.CreatePlan(input with
        {
            Selections = [new("ties", ContentImportMode.Replace)],
        });
        Equal(new[] { $"entity:{tieId}" }, PlanFamily(tieReplace, "ties").Protect.ToArray(),
            "group membership protects a replaced TIE");
        Equal(2, PlanFamily(tieReplace, "ties").Remove.Count,
            "attached collision does not protect its owning TIE from replacement");
        Equal(true, PlanFamily(tieReplace, "ties").Remove.Contains($"entity:{collisionId}"),
            "attached collision is included in the replacement preview");
        Equal(true, PlanFamily(tieReplace, "ties").Warnings.Any(value =>
                value.Contains("field group.members", StringComparison.Ordinal)),
            "protected TIE report identifies the inbound owner field");

        var skyReplace = UyaContentImportPlanningService.CreatePlan(input with
        {
            Selections = [new("skybox", ContentImportMode.Replace)],
        });
        Equal(new[] { $"entity:{skyId}" }, PlanFamily(skyReplace, "skybox").Protect.ToArray(),
            "group membership protects a replaced sky shell");
        Equal(true, PlanFamily(skyReplace, "skybox").Blockers.Any(value =>
                value.Contains("protected target shell", StringComparison.Ordinal)),
            "protected sky shell blocks exact singleton replacement");

        var geometryAndSettings = UyaContentImportPlanningService.CreatePlan(input with
        {
            Selections =
            [
                new("cuboids", ContentImportMode.Replace),
                new("levelSettings", ContentImportMode.Replace),
            ],
        });
        Equal(true, PlanFamily(geometryAndSettings, "cuboids").Remove.Contains($"entity:{cameraCuboidId}"),
            "replaced level settings no longer protect their old camera cuboid");

        var donorCuboidId = EntityId.New();
        var donorCuboid = cameraCuboid with { EntityId = donorCuboidId };
        var donorSettings = levelSettings with
        {
            Scene = levelSettings.Scene! with
            {
                ShipCameraCuboidStart = LevelReference(1,
                    ProjectReferences.LevelShipCameraCuboidStart, ProjectEntityKind.Cuboid, donorCuboidId),
                ShipCameraCuboidEnd = LevelReference(1,
                    ProjectReferences.LevelShipCameraCuboidEnd, ProjectEntityKind.Cuboid, donorCuboidId),
            },
        };
        var automatic = UyaContentImportPlanningService.AddLevelSettingsReferenceRequirements(
            geometryAndSettings, donorSettings, [donorCuboid], target);
        Equal(0, automatic.ReferenceRequirements!.Count,
            "selected donor cuboid satisfies level-settings references automatically");
        var settingsOnly = UyaContentImportPlanningService.CreatePlan(input with
        {
            Selections = [new("levelSettings", ContentImportMode.Replace)],
        });
        var explicitMappings = UyaContentImportPlanningService.AddLevelSettingsReferenceRequirements(
            settingsOnly, donorSettings, [donorCuboid], target);
        Equal(2, explicitMappings.ReferenceRequirements!.Count,
            "unselected donor geometry requires explicit level-settings mappings");

        var donorArea = new ProjectEntity(
            EntityId.New(), "Donor area", "areas", ProjectTransform.Identity, null,
            new("UYA", 40, "gameplay/core/areas", 0),
            Geometry: new(Area: new(new(0, 0, 0, 1), 0, [],
                [new(1, Reference: new(ProjectReferenceDomain.Entity, ProjectReferences.AreaCuboids, true,
                    EntityKind: ProjectEntityKind.Cuboid, EntityId: donorCuboidId))], [],
                [new(2, Reference: new(ProjectReferenceDomain.Entity, ProjectReferences.AreaCylinders, true,
                    EntityKind: ProjectEntityKind.Cylinder))], [])));
        var areaFamilies = Families(new Dictionary<string, UyaContentImportSourceFamily>(StringComparer.Ordinal)
        {
            ["cuboids"] = new("cuboids", ContentImportSourceState.Present,
                [new("source:cuboids:gameplay/core/cuboids:1")], 128),
            ["areas"] = new("areas", ContentImportSourceState.Present,
                [new("source:areas:gameplay/core/areas:0")], 96),
        });
        var areaPlan = UyaContentImportPlanningService.CreatePlan(input with
        {
            SourceFamilies = areaFamilies,
            Selections = [new("cuboids", ContentImportMode.Add), new("areas", ContentImportMode.Add)],
        });
        areaPlan = UyaContentImportPlanningService.AddAreaReferenceRequirements(
            areaPlan, [donorCuboid, donorArea], target);
        var areaMapping = areaPlan.ReferenceRequirements!.Single();
        Equal((0, ProjectReferences.AreaCylinders, 2),
            (areaMapping.OwnerSourceIndex, areaMapping.FieldKey, areaMapping.SourceValue),
            "selected donor shapes auto-map while unavailable area links require an explicit choice");
        Equal(0, areaMapping.CandidateEntityIds.Count, "area mapping exposes only compatible retained targets");

        var selectorTies = Enumerable.Range(0, 70).Select(index => tie with
        {
            EntityId = EntityId.New(),
            Name = $"TIE with unavailable light #{index}",
            Provenance = tie.Provenance! with { SourceIndex = index + 3 },
            TieLighting = new(1, []),
        }).ToArray();
        var importedTieGroupId = GroupId.New();
        var directionalTarget = target with
        {
            Entities = target.Entities.Concat(selectorTies).ToArray(),
            Groups = target.Groups.Append(new(
                importedTieGroupId, "Prior imported TIE group", selectorTies.Select(value => value.EntityId).ToArray()))
                .ToArray(),
            ImportBatches =
            [
                new(
                    ContentImportBatchSchema.CurrentVersion,
                    $"import-{new string('d', 32)}",
                    ContentImportPlanSchema.CurrentVersion,
                    ContentImportCapabilitySchema.CurrentVersion,
                    1,
                    "UYA", "NTSC-U", "1.00",
                    new string('a', 32), new string('b', 64), 8,
                    [new("ties", ContentImportMode.Replace)],
                    [$"group:{importedTieGroupId}"], [], [], []),
            ],
        };
        var directionalFamilies = Families(new Dictionary<string, UyaContentImportSourceFamily>(StringComparer.Ordinal)
        {
            ["directionalLights"] = new("directionalLights", ContentImportSourceState.Present,
                [new("source:directionalLights:gameplay/core/directional_lights:0")], 0x50),
        });
        var directionalPlan = UyaContentImportPlanningService.CreatePlan(input with
        {
            SourceFamilies = directionalFamilies,
            Target = directionalTarget,
            Selections = [new("directionalLights", ContentImportMode.Replace)],
        });
        var donorLight = new ProjectEntity(
            EntityId.New(), "Directional Light #0", "directional lights", ProjectTransform.Identity, null,
            new("UYA", 40, "gameplay/core/directional_lights", 0),
            Lighting: new(DirectionalLight: new(
                new(1, 1, 1, 1), new(0, 0, -1, 0), new(1, 1, 1, 1), new(0, 0, 1, 0))));
        directionalPlan = UyaContentImportPlanningService.AddDirectionalLightBlockers(
            directionalPlan, [donorLight], directionalTarget);
        var directionalBlockers = PlanFamily(directionalPlan, "directionalLights").Blockers;
        Equal(1, directionalBlockers.Count,
            "directional-light preview groups repeated selector conflicts");
        Equal(true, directionalBlockers.Single().Contains("70 retained target TIE(s)", StringComparison.Ordinal)
                && directionalBlockers.Single().Contains(
                    "does not fit the donor's 1-light array", StringComparison.Ordinal),
            "directional-light preview blocks an invalid retained TIE selector with an actionable summary");
        var replacementFamilies = Families(new Dictionary<string, UyaContentImportSourceFamily>(StringComparer.Ordinal)
        {
            ["ties"] = new("ties", ContentImportSourceState.Present,
                [new("source:ties:gameplay/core/tie_instances:0", assetB.ToString(), 1)], 128),
            ["directionalLights"] = directionalFamilies.Single(value => value.Family == "directionalLights"),
        });
        var replacementPlan = UyaContentImportPlanningService.CreatePlan(input with
        {
            SourceFamilies = replacementFamilies,
            Target = directionalTarget,
            Selections =
            [
                new("ties", ContentImportMode.Replace),
                new("directionalLights", ContentImportMode.Replace),
            ],
        });
        Equal(true, selectorTies.All(value => PlanFamily(replacementPlan, "ties").Remove
                .Contains($"entity:{value.EntityId}", StringComparer.Ordinal)),
            "replace removes members of groups owned by prior imports");
        var donorTie = selectorTies[0] with
        {
            EntityId = EntityId.New(),
            Provenance = selectorTies[0].Provenance! with { Level = 40, SourceIndex = 0 },
            TieLighting = new(0, []),
        };
        replacementPlan = UyaContentImportPlanningService.AddDirectionalLightBlockers(
            replacementPlan, [donorLight, donorTie], directionalTarget);
        Equal(false, PlanFamily(replacementPlan, "directionalLights").Blockers.Any(value =>
                value.Contains("selector", StringComparison.Ordinal)),
            "removed prior-import TIEs do not block donor directional lights");
        var sentinelTie = selectorTies[0] with { TieLighting = new(0x0f, []) };
        var sentinelTarget = target with { Entities = target.Entities.Append(sentinelTie).ToArray() };
        var sentinelPlan = UyaContentImportPlanningService.CreatePlan(input with
        {
            SourceFamilies = directionalFamilies,
            Target = sentinelTarget,
            Selections = [new("directionalLights", ContentImportMode.Replace)],
        });
        sentinelPlan = UyaContentImportPlanningService.AddDirectionalLightBlockers(
            sentinelPlan, [donorLight], sentinelTarget);
        Equal(false, PlanFamily(sentinelPlan, "directionalLights").Blockers.Any(value =>
                value.Contains("selector", StringComparison.Ordinal)),
            "directional-light preview accepts the retail no-light sentinel");

        var hudFamilies = Families(new Dictionary<string, UyaContentImportSourceFamily>(StringComparer.Ordinal)
        {
            ["hud"] = new("hud", ContentImportSourceState.Present,
                [new("source:hud:singleton")], 0x400),
        });
        var hudTarget = target with { Hud = new(ProjectHudSchema.CurrentVersion, 0, [], []) };
        var hudPlan = UyaContentImportPlanningService.CreatePlan(input with
        {
            SourceFamilies = hudFamilies,
            Target = hudTarget,
            Selections = [new("hud", ContentImportMode.Replace)],
        });
        Equal(new[] { "hud:project" }, PlanFamily(hudPlan, "hud").Remove.ToArray(),
            "HUD replacement previews the complete target inventory");
        Equal(Array.Empty<string>(), PlanFamily(hudPlan, "hud").Blockers.ToArray(),
            "HUD replacement is available when target storage exists");
        var missingHudPlan = UyaContentImportPlanningService.CreatePlan(input with
        {
            SourceFamilies = hudFamilies,
            Selections = [new("hud", ContentImportMode.Replace)],
        });
        Equal(true, PlanFamily(missingHudPlan, "hud").Blockers.Any(value =>
                value.Contains("no HUD storage", StringComparison.Ordinal)),
            "HUD replacement blocks targets without a writable HUD container");

        var fxFamilies = Families(new Dictionary<string, UyaContentImportSourceFamily>(StringComparer.Ordinal)
        {
            ["fx"] = new("fx", ContentImportSourceState.Present,
                [new("source:fx:singleton")], 0x800),
        });
        var fxTarget = target with { Fx = new(ProjectFxSchema.CurrentVersion, [], []) };
        var fxPlan = UyaContentImportPlanningService.CreatePlan(input with
        {
            SourceFamilies = fxFamilies,
            Target = fxTarget,
            Selections = [new("fx", ContentImportMode.Replace)],
        });
        Equal(new[] { "fx:project" }, PlanFamily(fxPlan, "fx").Remove.ToArray(),
            "FX replacement previews the complete target bank");
        Equal(Array.Empty<string>(), PlanFamily(fxPlan, "fx").Blockers.ToArray(),
            "FX replacement is available when target storage exists");
        var missingFxPlan = UyaContentImportPlanningService.CreatePlan(input with
        {
            SourceFamilies = fxFamilies,
            Selections = [new("fx", ContentImportMode.Replace)],
        });
        Equal(true, PlanFamily(missingFxPlan, "fx").Blockers.Any(value =>
                value.Contains("no FX bank", StringComparison.Ordinal)),
            "FX replacement blocks targets without a writable FX bank");

        var cycle = UyaContentImportPlanningService.CreatePlan(input with
        {
            Selections = [new("mobys", ContentImportMode.Replace)],
        });
        Equal(2, PlanFamily(cycle, "mobys").Remove.Count,
            "bounded reference analysis handles a replacement cycle");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Throws<OperationCanceledException>(() => UyaContentImportPlanningService.CreatePlan(input, cancelled.Token),
            "cancelled plan does not traverse inventory");

        var large = Families(new Dictionary<string, UyaContentImportSourceFamily>(StringComparer.Ordinal)
        {
            ["mapGroups"] = new("mapGroups", ContentImportSourceState.Present,
                new[] { new UyaContentImportSourceItem("source:mapGroups:tie:table", ItemCount: 0) }
                    .Concat(Enumerable.Range(0, ProjectGroupSchema.MaximumGroupCount + 1)
                    .Select(index => new UyaContentImportSourceItem($"source:mapGroups:tie:{index}")))
                    .ToArray(), 1_000_000),
        });
        var capacity = UyaContentImportPlanningService.CreatePlan(input with
        {
            SourceFamilies = large,
            Selections = [new("mapGroups", ContentImportMode.Replace)],
        });
        Equal(true, PlanFamily(capacity, "mapGroups").Blockers.Any(value => value.Contains("4096 item limit")),
            "capacity blocker");

        var groupDependencies = Families(new Dictionary<string, UyaContentImportSourceFamily>(StringComparer.Ordinal)
        {
            ["ties"] = new("ties", ContentImportSourceState.Present,
                [new("source:gameplay/core/tie_instances:0")], 0x70),
            ["mapGroups"] = new("mapGroups", ContentImportSourceState.Present,
                [new("source:mapGroups:tie:table", ItemCount: 0)], 0x20),
        });
        var missingGroupMembers = UyaContentImportPlanningService.CreatePlan(input with
        {
            SourceFamilies = groupDependencies,
            Selections = [new("mapGroups", ContentImportMode.Replace)],
        });
        Equal(true, PlanFamily(missingGroupMembers, "mapGroups").Blockers.Any(value =>
                value.Contains("TIEs to be selected in replace mode", StringComparison.Ordinal)),
            "map groups require their member family");
        var completeGroupMembers = UyaContentImportPlanningService.CreatePlan(input with
        {
            SourceFamilies = groupDependencies,
            Target = target with { Groups = [] },
            Selections =
            [
                new("ties", ContentImportMode.Replace),
                new("mapGroups", ContentImportMode.Replace),
            ],
        });
        Equal(false, PlanFamily(completeGroupMembers, "mapGroups").Blockers.Any(value =>
                value.Contains("TIEs", StringComparison.Ordinal)),
            "map groups accept a complete member-family replacement");

        var occlusionFamilies = Families(new Dictionary<string, UyaContentImportSourceFamily>(StringComparer.Ordinal)
        {
            ["occlusion"] = new("occlusion", ContentImportSourceState.Present,
                [new("source:occlusion:singleton")], 0x100),
        });
        var occlusionOnly = UyaContentImportPlanningService.CreatePlan(input with
        {
            SourceFamilies = occlusionFamilies,
            Selections = [new("occlusion", ContentImportMode.Replace)],
        });
        Equal(3, PlanFamily(occlusionOnly, "occlusion").Blockers.Count(value =>
                value.Contains("selected in replace mode", StringComparison.Ordinal)),
            "occlusion requires complete TIE, Moby, and tfrag replacement");

        var request = new ContentImportPlanRequestPayload(
            "project", "catalog", "source.iso", new string('a', 32), 40,
            [new("ties", (uint)ContentImportMode.Add)]);
        var requestBytes = BridgePayloadCodec.EncodeContentImportPlanRequest(request);
        var decodedRequest = BridgePayloadCodec.DecodeContentImportPlanRequest(requestBytes);
        Equal(request.ProjectPath, decodedRequest.ProjectPath, "plan request project path");
        Equal(request.SourceLevel, decodedRequest.SourceLevel, "plan request source level");
        Equal(request.Selections.ToArray(), decodedRequest.Selections.ToArray(),
            "plan request selections");
        Equal(requestBytes, BridgePayloadCodec.EncodeContentImportPlanRequest(decodedRequest),
            "plan request bridge bytes");
        var payload = PlanPayload(plan);
        var payloadBytes = BridgePayloadCodec.EncodeContentImportPlan(payload);
        var decoded = BridgePayloadCodec.DecodeContentImportPlan(payloadBytes);
        Equal(payload.BatchId, decoded.BatchId, "plan bridge batch ID");
        Equal(payload.Families.Count, decoded.Families.Count, "plan bridge families");
        Equal("TIEs", decoded.Families.Single(value => value.Family == "ties").Label,
            "plan bridge preserves renderer-neutral labels");
        Equal((uint)ContentImportMode.AddAndReplace,
            decoded.Families.Single(value => value.Family == "ties").SupportedMode,
            "plan bridge preserves supported modes");
        Equal(payloadBytes, BridgePayloadCodec.EncodeContentImportPlan(decoded),
            "plan bridge bytes are deterministic");
        var widePlan = plan with
        {
            ReferenceRequirements =
            [
                new("levelSettings", -1, ProjectReferences.LevelShipCameraCuboidStart, 1,
                    ProjectEntityKind.Cuboid, true,
                    Enumerable.Range(0, 65).Select(_ => EntityId.New()).ToArray()),
            ],
        };
        var widePayload = BridgePayloadCodec.DecodeContentImportPlan(
            BridgePayloadCodec.EncodeContentImportPlan(PlanPayload(widePlan)));
        Equal(65, widePayload.ReferenceRequirements.Single().CandidateEntityIds.Count,
            "reference candidate lists use the import-plan item limit");
    }

    private static IReadOnlyList<UyaContentImportSourceFamily> Families(
        IReadOnlyDictionary<string, UyaContentImportSourceFamily> replacements)
    {
        var order = UyaContentImportCapabilities.Create("NTSC-U", "1.00").Families
            .Select(value => value.Family);
        return order.Select(family => replacements.GetValueOrDefault(family)
            ?? new(family, ContentImportSourceState.Empty, [], 0)).ToArray();
    }

    private static ContentImportFamilyPlan PlanFamily(ContentImportPlan plan, string family) =>
        plan.Families.Single(value => value.Family == family);

    private static ProjectIndexedReference LevelReference(
        int sourceValue,
        string fieldKey,
        ProjectEntityKind kind,
        EntityId? entityId) => new(sourceValue, new(
        ProjectReferenceDomain.Entity, fieldKey, true, EntityKind: kind, EntityId: entityId));

    private static ContentImportPlanPayload PlanPayload(ContentImportPlan plan) => new(
        (uint)plan.SchemaVersion,
        (uint)plan.CapabilitySchemaVersion,
        plan.BatchId,
        plan.Game,
        plan.Region,
        plan.Revision,
        plan.SourceFingerprint,
        plan.SourceLevelHash,
        (uint)plan.SourceLevel,
        plan.TargetProjectId,
        plan.TargetFingerprint,
        (uint)plan.TargetLevel,
        (ulong)plan.EstimatedBytes,
        plan.Families.Select(value => new ContentImportFamilyPlanPayload(
            value.Family, value.Label, (uint)value.SupportedMode,
            (uint)value.Mode, (uint)value.SourceState, value.Selected,
            value.DefaultSelected, value.Selectable, value.Add, value.Reuse, value.Remap,
            value.Remove, value.Protect, value.DependencyReasons, value.Conflicts,
            (ulong)value.EstimatedBytes, value.Warnings, value.Blockers)).ToArray(),
        plan.Warnings,
        plan.Blockers,
        (plan.ReferenceRequirements ?? []).Select(value => new ContentImportReferenceRequirementPayload(
            value.Family, value.OwnerSourceIndex, value.FieldKey, value.SourceValue,
            (uint)value.TargetKind, value.Nullable,
            value.CandidateEntityIds.Select(candidate => candidate.ToString()).ToArray())).ToArray());

    private static ContentImportFamilyCapability Family(ContentImportCapabilityMatrix matrix, string family) =>
        matrix.Families.Single(value => value.Family == family);

    private static void Equal<T>(T expected, T actual, string name)
    {
        if (expected is Array expectedArray && actual is Array actualArray)
        {
            if (expectedArray.Length == actualArray.Length
                && expectedArray.Cast<object?>().SequenceEqual(actualArray.Cast<object?>())) return;
        }
        else if (EqualityComparer<T>.Default.Equals(expected, actual)) return;
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
    }

    private static void Throws<TException>(Action action, string name) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"{name}: expected {typeof(TException).Name}");
    }
}

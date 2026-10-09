using Forge.Host.Games.UYA;
using Forge.Host.ProtocolTests.Games.UYA;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Forge.Host.Bridge;
using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Sdk;

internal static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "bridge-v4.json"));
            var vectors = JsonSerializer.Deserialize<List<GoldenFrame>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? throw new InvalidOperationException("Golden vectors are empty");

            VerifyGoldenVectors(vectors);
            VerifyFragmentation(vectors);
            VerifyInvalidInput(vectors);
            VerifyPayloads();
            VerifyIdentityAndSchema();
            await UyaIsoSetupTests.RunAsync();
            await AssetCatalogTests.RunAsync();
            await AssetMaintenanceTests.RunAsync();
            await UyaAssetImportTests.RunAsync();
            await ForgeProjectTests.RunAsync();
            await ProjectReferenceTests.RunAsync();
            await AuthoringFoundationQualificationTests.RunAsync();
            await HudProjectTests.RunAsync();
            await FxProjectTests.RunAsync();
            await BakeLayerTests.RunAsync();
            await EditorRuntimeTests.RunAsync();
            await UyaProjectTests.RunAsync();
            await UyaBakeWorkflowTests.RunAsync();
            await UyaRenderPackageTests.RunAsync();
            await UyaArchiveBuildTests.RunAsync();
            await UyaStaticLayerTests.RunAsync();
            await UyaGameplayLayerTests.RunAsync();
            Console.WriteLine("C# bridge and domain contract checks passed");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void VerifyGoldenVectors(IReadOnlyList<GoldenFrame> vectors)
    {
        foreach (var vector in vectors)
        {
            var frame = vector.ToFrame();
            Equal(vector.FrameHex, Convert.ToHexString(BridgeFrameCodec.Encode(frame)).ToLowerInvariant(), vector.Name);

            var decoder = new BridgeFrameDecoder();
            var decoded = decoder.Push(Convert.FromHexString(vector.FrameHex)).Single();
            decoder.Complete();
            Equal(frame, decoded, vector.Name);
        }

        Equal("bad", BridgeErrorPayload.Decode(vectors[^1].ToFrame().Payload), "error message");
    }

    private static void VerifyFragmentation(IReadOnlyList<GoldenFrame> vectors)
    {
        var stream = vectors.SelectMany(vector => Convert.FromHexString(vector.FrameHex)).ToArray();
        var fragmented = new BridgeFrameDecoder();
        var fragmentedFrames = new List<BridgeFrame>();
        foreach (var value in stream) fragmentedFrames.AddRange(fragmented.Push([value]));
        fragmented.Complete();
        Equal(vectors.Count, fragmentedFrames.Count, "fragmented frame count");

        var coalesced = new BridgeFrameDecoder();
        Equal(vectors.Count, coalesced.Push(stream).Count, "coalesced frame count");
        coalesced.Complete();
    }

    private static void VerifyInvalidInput(IReadOnlyList<GoldenFrame> vectors)
    {
        var validHeader = Convert.FromHexString(vectors[1].FrameHex)[..BridgeFrameCodec.HeaderSize];

        var badMagic = validHeader.ToArray();
        badMagic[0] ^= 0xff;
        Expect(BridgeErrorCode.InvalidMagic, () => new BridgeFrameDecoder().Push(badMagic));

        var badVersion = validHeader.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(badVersion.AsSpan(4), 1);
        Expect(BridgeErrorCode.UnsupportedVersion, () => new BridgeFrameDecoder().Push(badVersion));

        var badOpcode = validHeader.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(badOpcode.AsSpan(8), ushort.MaxValue);
        Expect(BridgeErrorCode.UnknownOpcode, () => new BridgeFrameDecoder().Push(badOpcode));

        var oversized = validHeader.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(16), BridgeFrameCodec.MaxPayloadLength + 1u);
        Expect(BridgeErrorCode.PayloadTooLarge, () => new BridgeFrameDecoder().Push(oversized));

        var truncated = new BridgeFrameDecoder();
        truncated.Push(Convert.FromHexString(vectors[1].FrameHex)[..(BridgeFrameCodec.HeaderSize + 1)]);
        Expect(BridgeErrorCode.MalformedPayload, truncated.Complete);

        var malformedError = Convert.FromHexString(vectors[^1].FrameHex);
        BinaryPrimitives.WriteUInt32LittleEndian(malformedError.AsSpan(BridgeFrameCodec.HeaderSize), 4);
        Expect(BridgeErrorCode.MalformedPayload, () => new BridgeFrameDecoder().Push(malformedError));
    }

    private static void VerifyPayloads()
    {
        var handshake = new HostHandshake("0.1.0", "abc123", ["UYA"], ["bridge.echo"]);
        var decodedHandshake = BridgePayloadCodec.DecodeHandshake(BridgePayloadCodec.EncodeHandshake(handshake));
        Equal(handshake.HostVersion, decodedHandshake.HostVersion, "handshake host version");
        Equal(handshake.SdkRevision, decodedHandshake.SdkRevision, "handshake SDK revision");
        Equal(true, handshake.SupportedGames.SequenceEqual(decodedHandshake.SupportedGames), "handshake games");
        Equal(true, handshake.Capabilities.SequenceEqual(decodedHandshake.Capabilities), "handshake capabilities");
        Equal(new EchoRequest("hello", 50), BridgePayloadCodec.DecodeEchoRequest(
            BridgePayloadCodec.EncodeEchoRequest("hello", 50)), "echo payload");
        Equal("result", BridgePayloadCodec.DecodeText(BridgePayloadCodec.EncodeText("result")), "text payload");
        var fxCanonical = "canonical FX texture"u8.ToArray();
        var fxInventory = new FxTextureInventory(
            GameId.UYA,
            new(true, false, false, "writer pending"),
            [
                new(0, "FX_LAME_SHADOW", 32, 32, "Indexed8", "Rgba32", 0, 0x400, 0x400, 0x400,
                    false, true, null, fxCanonical),
                new(1, "FX_CLOUDY_CIRCLE_1", 3, 32, "Indexed8", "Rgba32", 0x800, 0x400, 0xc00, 0,
                    false, false, "FX texture 1 dimensions are invalid.", []),
            ]);
        var fxPayload = BridgePayloadCodec.ToPayload(fxInventory);
        Equal(AssetId.Compute(
                AssetKind.Texture, ProjectTextureAssetSchema.CanonicalFormatVersion, fxCanonical).ToString(),
            fxPayload.Entries[0].SourceAssetId, "FX bridge payload exact source texture identity");
        Equal(null, fxPayload.Entries[1].SourceAssetId, "invalid FX bridge entry has no source identity");
        var decodedFxPayload = BridgePayloadCodec.DecodeFxTextureInventory(
            BridgePayloadCodec.EncodeFxTextureInventory(fxPayload));
        Equal(fxPayload with { Entries = decodedFxPayload.Entries }, decodedFxPayload,
            "FX inventory bridge payload");
        Equal(new BridgeProgress(2, 10), BridgePayloadCodec.DecodeProgress(
            BridgePayloadCodec.EncodeProgress(2, 10)), "progress payload");
        var iso = new UyaIsoValidationPayload(true, "UYA", "NTSC-U", "1.00", "SCUS-97353", 4_379_377_664,
            UyaIsoService.SupportedMd5, "verified");
        Equal(iso, BridgePayloadCodec.DecodeUyaIsoValidation(
            BridgePayloadCodec.EncodeUyaIsoValidation(iso)), "ISO validation payload");
        var copyRequest = new DevelopmentIsoRequest("source.iso", "target.iso", UyaIsoService.SupportedMd5, true);
        Equal(copyRequest, BridgePayloadCodec.DecodeDevelopmentIsoRequest(
            BridgePayloadCodec.EncodeDevelopmentIsoRequest(copyRequest)), "development ISO request payload");
        var copyResult = new DevelopmentIsoPayload("target.iso", 4_379_377_664, UyaIsoService.SupportedMd5);
        Equal(copyResult, BridgePayloadCodec.DecodeDevelopmentIso(
            BridgePayloadCodec.EncodeDevelopmentIso(copyResult)), "development ISO result payload");
        var importRequest = new UyaAssetImportRequestPayload(
            "source.iso", "assets", UyaIsoService.SupportedMd5, "1.00", true);
        Equal(importRequest, BridgePayloadCodec.DecodeUyaAssetImportRequest(
            BridgePayloadCodec.EncodeUyaAssetImportRequest(importRequest)), "asset import request payload");
        var importResult = new UyaAssetImportResultPayload(40, 40, 900, 500, 2, true);
        Equal(importResult, BridgePayloadCodec.DecodeUyaAssetImportResult(
            BridgePayloadCodec.EncodeUyaAssetImportResult(importResult)), "asset import result payload");
        var options = new UyaProjectOptionsPayload([1, 3, 5], ["partial"]);
        var decodedOptions = BridgePayloadCodec.DecodeUyaProjectOptions(BridgePayloadCodec.EncodeUyaProjectOptions(options));
        Equal(true, options.Levels.SequenceEqual(decodedOptions.Levels), "project option levels");
        Equal(true, options.Warnings.SequenceEqual(decodedOptions.Warnings), "project option warnings");
        var createProject = new UyaProjectCreationRequestPayload(
            "source.iso", "assets", "project", "Test", UyaIsoService.SupportedMd5, "1.00", 3, true);
        Equal(createProject, BridgePayloadCodec.DecodeUyaProjectCreationRequest(
            BridgePayloadCodec.EncodeUyaProjectCreationRequest(createProject)), "project creation payload");
        var preflightRequest = new UyaProjectPreflightRequestPayload("source.iso", "assets", 3);
        Equal(preflightRequest, BridgePayloadCodec.DecodeUyaProjectPreflightRequest(
            BridgePayloadCodec.EncodeUyaProjectPreflightRequest(preflightRequest)), "project preflight request payload");
        var preflight = new UyaProjectPreflightPayload(3, 422, 378, 44, 0, 0, ["partial"]);
        var decodedPreflight = BridgePayloadCodec.DecodeUyaProjectPreflight(
            BridgePayloadCodec.EncodeUyaProjectPreflight(preflight));
        Equal(preflight with { Warnings = decodedPreflight.Warnings }, decodedPreflight, "project preflight payload");
        var inspectProject = new ProjectInspectRequestPayload("project", "assets");
        Equal(inspectProject, BridgePayloadCodec.DecodeProjectInspectRequest(
            BridgePayloadCodec.EncodeProjectInspectRequest(inspectProject)), "project inspect payload");
        var renameProject = new ProjectRenameRequestPayload("project", "assets", "Renamed");
        Equal(renameProject, BridgePayloadCodec.DecodeProjectRenameRequest(
            BridgePayloadCodec.EncodeProjectRenameRequest(renameProject)), "project rename payload");
        var recoveryRequest = new ProjectRecoveryRequestPayload("project", "assets", "1000-0123456789abcdef0123456789abcdef");
        Equal(recoveryRequest, BridgePayloadCodec.DecodeProjectRecoveryRequest(
            BridgePayloadCodec.EncodeProjectRecoveryRequest(recoveryRequest)), "project recovery request payload");
        var summary = new ForgeProjectSummaryPayload(
            "project", "Test", "UYA", "NTSC-U", "1.00", "uya-ntsc-u", 3, 1000, true);
        Equal(summary, BridgePayloadCodec.DecodeForgeProjectSummary(
            BridgePayloadCodec.EncodeForgeProjectSummary(summary)), "project summary payload");
        var repairRequest = new ProjectAssetRepairRequestPayload("project", "assets", "source.iso");
        Equal(repairRequest, BridgePayloadCodec.DecodeProjectAssetRepairRequest(
            BridgePayloadCodec.EncodeProjectAssetRepairRequest(repairRequest)), "project repair request payload");
        var maintenanceRequest = new CatalogMaintenanceRequestPayload("assets", ["projects", "external"]);
        var decodedMaintenanceRequest = BridgePayloadCodec.DecodeCatalogMaintenanceRequest(
            BridgePayloadCodec.EncodeCatalogMaintenanceRequest(maintenanceRequest));
        Equal(true, maintenanceRequest.ProjectRoots.SequenceEqual(decodedMaintenanceRequest.ProjectRoots),
            "catalog maintenance roots");
        var collectionRequest = new CatalogCollectionRequestPayload("assets", ["projects"], new string('c', 64));
        var decodedCollectionRequest = BridgePayloadCodec.DecodeCatalogCollectionRequest(
            BridgePayloadCodec.EncodeCatalogCollectionRequest(collectionRequest));
        Equal(collectionRequest with { ProjectRoots = decodedCollectionRequest.ProjectRoots }, decodedCollectionRequest,
            "catalog collection request payload");
        var maintenance = new CatalogMaintenancePayload(
            2, 500, 450, 50, 48, 123_456, collectionRequest.ConfirmationToken, ["Moby: 30", "Tie: 20"], []);
        var decodedMaintenance = BridgePayloadCodec.DecodeCatalogMaintenance(
            BridgePayloadCodec.EncodeCatalogMaintenance(maintenance));
        Equal(maintenance with
            {
                CandidateKinds = decodedMaintenance.CandidateKinds,
                Blockers = decodedMaintenance.Blockers,
            }, decodedMaintenance, "catalog maintenance payload");
        var explorerRequest = new AssetExplorerRequestPayload(
            "assets", AssetExplorerCategoryPayload.Ties, "crate", "UYA", "level03", "NTSC-U", "1.00",
            ["vanilla"], "cursor", 64, "UYA", "NTSC-U", "1.00", 3);
        var decodedExplorerRequest = AssetExplorerPayloadCodec.DecodeRequest(
            AssetExplorerPayloadCodec.EncodeRequest(explorerRequest));
        Equal(explorerRequest with { Tags = decodedExplorerRequest.Tags }, decodedExplorerRequest,
            "asset explorer request payload");
        Equal(true, explorerRequest.Tags.SequenceEqual(decodedExplorerRequest.Tags), "asset explorer request tags");
        var explorerPage = new AssetExplorerPagePayload(
            [new(
                new string('d', 64), AssetExplorerCategoryPayload.Textures, "moby:0x0123 material 0", 1, 4096,
                ["moby:0x0123 material 0"], ["vanilla"],
                [new("UYA", "NTSC-U", "1.00", "level03", "level_wad/assets/asset_wad.bin", 7,
                    new("moby", 291, "material", 0, true))],
                [], "notCached", false, "Textures are preview-only.")],
            new(["UYA"], ["level03"], ["NTSC-U"], ["1.00"], ["vanilla"]),
            "next");
        var decodedExplorerPage = AssetExplorerPayloadCodec.DecodePage(
            AssetExplorerPayloadCodec.EncodePage(explorerPage));
        Equal(explorerPage.Items[0] with
        {
            Aliases = decodedExplorerPage.Items[0].Aliases,
            Tags = decodedExplorerPage.Items[0].Tags,
            Sources = decodedExplorerPage.Items[0].Sources,
            ClassIds = decodedExplorerPage.Items[0].ClassIds,
        }, decodedExplorerPage.Items[0], "asset explorer item payload");
        Equal(true, explorerPage.Items[0].Aliases.SequenceEqual(decodedExplorerPage.Items[0].Aliases),
            "asset explorer aliases");
        Equal(true, explorerPage.Items[0].Sources.SequenceEqual(decodedExplorerPage.Items[0].Sources),
            "asset explorer sources");
        Equal(true, explorerPage.Facets.Tags.SequenceEqual(decodedExplorerPage.Facets.Tags),
            "asset explorer facets");
        var recovery = new ProjectRecoverySnapshotPayload(
            recoveryRequest.RecoveryId, 1000, "Recovered", 20, new string('a', 64), 4096);
        var descriptor = new ForgeProjectDescriptorPayload(
            "project", "Test", "UYA", "NTSC-U", "1.00", "uya-ntsc-u", 3, 1000, 20, 0,
            false, false, ["partial"], [recovery],
            [new(new string('b', 64), "Moby", 2, true, ["UYA level 3"])]);
        var decodedDescriptor = BridgePayloadCodec.DecodeForgeProjectDescriptor(
            BridgePayloadCodec.EncodeForgeProjectDescriptor(descriptor));
        Equal(true, descriptor.Warnings.SequenceEqual(decodedDescriptor.Warnings), "project descriptor warnings");
        Equal(true, descriptor.Recoveries.SequenceEqual(decodedDescriptor.Recoveries), "project descriptor recoveries");
        Equal(descriptor.MissingAssets[0] with { Provenance = decodedDescriptor.MissingAssets[0].Provenance },
            decodedDescriptor.MissingAssets[0], "project descriptor missing asset");
        Equal(true, descriptor.MissingAssets[0].Provenance.SequenceEqual(decodedDescriptor.MissingAssets[0].Provenance),
            "project descriptor missing provenance");
        Equal(descriptor with
            {
                Warnings = decodedDescriptor.Warnings,
                Recoveries = decodedDescriptor.Recoveries,
                MissingAssets = decodedDescriptor.MissingAssets,
            },
            decodedDescriptor, "project descriptor payload");

        var renderRequest = new UyaRenderPackageRequestPayload(
            "source.iso", "render-cache", UyaIsoService.SupportedMd5, 3, "project", "catalog");
        var decodedRenderRequest = BridgePayloadCodec.DecodeUyaRenderPackageRequest(
            BridgePayloadCodec.EncodeUyaRenderPackageRequest(renderRequest));
        Equal(renderRequest, decodedRenderRequest, "render-package request payload");
        var renderResult = new RenderPackageResultPayload(
            "render-cache/key", "key", ["tfrag/tfrag.gltf", "tfrag/chunks/chunk1/tfrag.gltf"],
            "assets/skybox/skybox.gltf",
            new(57, 65, 50, 40, 50, 40, 10, 175, 255, 0,
                -20, false, 0, 0, 0, 1, 2, 3, 1.5f, 2, 3, 4, 2, 5),
            [
                new(new string('a', 64), "moby", "entities/a/model.gltf", null),
                new(new string('b', 64), "tie", null, "missing asset"),
            ],
            true,
            [new(1, 2, 3, 4)]);
        var decodedRenderResult = BridgePayloadCodec.DecodeRenderPackageResult(
            BridgePayloadCodec.EncodeRenderPackageResult(renderResult));
        Equal(renderResult with
            {
                TerrainPaths = decodedRenderResult.TerrainPaths,
                Assets = decodedRenderResult.Assets,
                OcclusionOctants = decodedRenderResult.OcclusionOctants,
            },
            decodedRenderResult, "render-package result payload");
        Equal(true, renderResult.TerrainPaths.SequenceEqual(decodedRenderResult.TerrainPaths),
            "render-package terrain paths");
        Equal(true, renderResult.Assets.SequenceEqual(decodedRenderResult.Assets),
            "render-package assets");
        Equal(true, renderResult.OcclusionOctants!.SequenceEqual(decodedRenderResult.OcclusionOctants!),
            "render-package occlusion octants");

        var assetPreviewRequest = new AssetPreviewRequestPayload(
            "render-cache", "catalog", "project", new string('c', 64), "sky", "UYA", "sky-default", 2);
        Equal(assetPreviewRequest, BridgePayloadCodec.DecodeAssetPreviewRequest(
            BridgePayloadCodec.EncodeAssetPreviewRequest(assetPreviewRequest)), "asset-preview request payload");
        var assetPreviewResult = new AssetPreviewResultPayload(
            "render-cache/uya-preview-key", "uya-preview-key", "model.gltf", true);
        Equal(assetPreviewResult, BridgePayloadCodec.DecodeAssetPreviewResult(
            BridgePayloadCodec.EncodeAssetPreviewResult(assetPreviewResult)), "asset-preview result payload");

        var buildRequest = new UyaBuildPatchRequestPayload(
            "project", "catalog", "clean.iso", "development.iso", new string('a', 32), ["WARN-1"], false,
            ["Ties", "Lighting"], true);
        var decodedBuildRequest = BuildPayloadCodec.DecodeRequest(BuildPayloadCodec.EncodeRequest(buildRequest));
        Equal(buildRequest with
        {
            AcknowledgedWarnings = decodedBuildRequest.AcknowledgedWarnings,
            IncludedLayers = decodedBuildRequest.IncludedLayers,
        },
            decodedBuildRequest, "build request payload");
        var buildPlan = new BuildPlanPayload([
            new("Ties", "Dirty", true),
            new("Lighting", "DependencyInvalidated", false),
        ]);
        var decodedBuildPlan = BuildPayloadCodec.DecodePlan(BuildPayloadCodec.EncodePlan(buildPlan));
        Equal(true, buildPlan.Layers.SequenceEqual(decodedBuildPlan.Layers), "build plan payload");
        var buildProgress = new UyaBuildPatchProgressPayload("Pack", 2, 4, "Packing");
        Equal(buildProgress, BuildPayloadCodec.DecodeProgress(BuildPayloadCodec.EncodeProgress(buildProgress)),
            "build progress payload");
        var parentEntityId = EntityId.New();
        var collisionPreviewWriter = new PayloadWriter();
        collisionPreviewWriter.WriteString(parentEntityId.ToString());
        collisionPreviewWriter.WriteBoolean(true);
        collisionPreviewWriter.WriteUInt32(0xa7);
        collisionPreviewWriter.WriteUInt32(9);
        collisionPreviewWriter.WriteUInt32(2);
        collisionPreviewWriter.WriteBoolean(true);
        var collisionPreviewRequest = EditorPayloadCodec.DecodeInstancedCollisionPreviewRequest(collisionPreviewWriter.ToArray());
        Equal(parentEntityId, collisionPreviewRequest.EntityId, "instanced collision preview entity payload");
        Equal(new EditorInstancedCollisionGenerationSettings(0xa7, 9, 1, true), collisionPreviewRequest.Settings,
            "instanced collision generation settings payload");
        var invalidCollisionPreviewWriter = new PayloadWriter();
        invalidCollisionPreviewWriter.WriteString(parentEntityId.ToString());
        invalidCollisionPreviewWriter.WriteBoolean(true);
        invalidCollisionPreviewWriter.WriteUInt32(256);
        invalidCollisionPreviewWriter.WriteUInt32(6);
        invalidCollisionPreviewWriter.WriteUInt32(0);
        invalidCollisionPreviewWriter.WriteBoolean(false);
        Expect(BridgeErrorCode.MalformedPayload, () =>
            EditorPayloadCodec.DecodeInstancedCollisionPreviewRequest(invalidCollisionPreviewWriter.ToArray()));

        var collisionFaceCommandId = Guid.NewGuid().ToString("D");
        var collisionFaceProxyId = new string('d', AssetId.TextLength);
        var collisionFaceCommandWriter = new PayloadWriter();
        collisionFaceCommandWriter.WriteString(collisionFaceCommandId);
        collisionFaceCommandWriter.WriteUInt32((uint)EditorCommandKind.SetInstancedCollisionFaceTypes);
        collisionFaceCommandWriter.WriteUInt32(1);
        collisionFaceCommandWriter.WriteString(parentEntityId.ToString());
        collisionFaceCommandWriter.WriteBoolean(false);
        collisionFaceCommandWriter.WriteUInt32(0);
        for (var index = 0; index < 10; index++) collisionFaceCommandWriter.WriteBoolean(false);
        collisionFaceCommandWriter.WriteBoolean(true);
        collisionFaceCommandWriter.WriteString(collisionFaceProxyId);
        collisionFaceCommandWriter.WriteUInt32(2);
        collisionFaceCommandWriter.WriteUInt32(7);
        collisionFaceCommandWriter.WriteUInt32(0xaf);
        collisionFaceCommandWriter.WriteUInt32(2);
        collisionFaceCommandWriter.WriteUInt32(0x31);
        collisionFaceCommandWriter.WriteBoolean(false);
        collisionFaceCommandWriter.WriteBoolean(false);
        collisionFaceCommandWriter.WriteBoolean(false);
        collisionFaceCommandWriter.WriteBoolean(false);
        var collisionFaceCommand = EditorPayloadCodec.DecodeCommand(collisionFaceCommandWriter.ToArray());
        Equal(collisionFaceCommandId, collisionFaceCommand.Id, "collision face command ID payload");
        Equal(EditorCommandKind.SetInstancedCollisionFaceTypes, collisionFaceCommand.Kind,
            "collision face command kind payload");
        Equal(AssetId.Parse(collisionFaceProxyId), collisionFaceCommand.InstancedCollisionProxyAssetId,
            "collision face command proxy payload");
        Equal(true, collisionFaceCommand.InstancedCollisionFaceTypes!.SequenceEqual([new(7, 0xaf), new(2, 0x31)]),
            "collision face command assignment payload");
        var referenceCommandWriter = new PayloadWriter();
        referenceCommandWriter.WriteString(Guid.NewGuid().ToString("D"));
        referenceCommandWriter.WriteUInt32((uint)EditorCommandKind.SetEntityReference);
        referenceCommandWriter.WriteUInt32(1);
        referenceCommandWriter.WriteString(parentEntityId.ToString());
        referenceCommandWriter.WriteBoolean(false);
        referenceCommandWriter.WriteUInt32(0);
        for (var index = 0; index < 11; index++) referenceCommandWriter.WriteBoolean(false);
        referenceCommandWriter.WriteBoolean(true);
        referenceCommandWriter.WriteString(ProjectReferences.AreaSplines);
        referenceCommandWriter.WriteBoolean(true);
        referenceCommandWriter.WriteUInt32(7);
        referenceCommandWriter.WriteBoolean(true);
        referenceCommandWriter.WriteString(parentEntityId.ToString());
        referenceCommandWriter.WriteBoolean(false);
        referenceCommandWriter.WriteBoolean(false);
        referenceCommandWriter.WriteBoolean(false);
        var referenceCommand = EditorPayloadCodec.DecodeCommand(referenceCommandWriter.ToArray());
        Equal(EditorCommandKind.SetEntityReference, referenceCommand.Kind, "reference command kind payload");
        Equal(new EditorReferenceUpdate(ProjectReferences.AreaSplines, 7, parentEntityId),
            referenceCommand.ReferenceUpdate, "reference command payload");
        var hudSourceId = new string('e', AssetId.TextLength);
        var hudImage = new byte[] { 0x89, 0x50, 0x4e, 0x47 };
        var hudCommandWriter = new PayloadWriter();
        hudCommandWriter.WriteString(Guid.NewGuid().ToString("D"));
        hudCommandWriter.WriteUInt32((uint)EditorCommandKind.ReplaceHudTexture);
        hudCommandWriter.WriteUInt32(0);
        hudCommandWriter.WriteBoolean(false);
        hudCommandWriter.WriteUInt32(0);
        for (var index = 0; index < 13; index++) hudCommandWriter.WriteBoolean(false);
        hudCommandWriter.WriteBoolean(true);
        hudCommandWriter.WriteBoolean(true);
        hudCommandWriter.WriteString(hudSourceId);
        hudCommandWriter.WriteBoolean(false);
        hudCommandWriter.WriteBoolean(false);
        hudCommandWriter.WriteBoolean(true);
        hudCommandWriter.WriteString("png");
        hudCommandWriter.WriteBoolean(true);
        hudCommandWriter.WriteBytes(hudImage, 16 * 1024 * 1024);
        hudCommandWriter.WriteBoolean(false);
        var hudCommand = EditorPayloadCodec.DecodeCommand(hudCommandWriter.ToArray());
        Equal(EditorCommandKind.ReplaceHudTexture, hudCommand.Kind, "HUD command kind payload");
        Equal(AssetId.Parse(hudSourceId), hudCommand.HudEdit!.SourceAssetId, "HUD command source payload");
        Equal(true, hudImage.SequenceEqual(hudCommand.HudEdit.ImageBytes!), "HUD command image payload");
        var buildResult = new UyaBuildPatchResultPayload(
            true, false, [], ["diagnostic"], "Patched", "Reload", "development.iso", "InPlace",
            new string('b', 64), 2, false);
        var decodedBuildResult = BuildPayloadCodec.DecodeResult(BuildPayloadCodec.EncodeResult(buildResult));
        Equal(buildResult with
        {
            WarningCodes = decodedBuildResult.WarningCodes,
            Diagnostics = decodedBuildResult.Diagnostics,
        }, decodedBuildResult, "build result payload");

        var trailing = BridgePayloadCodec.EncodeText("x").Append((byte)0).ToArray();
        Expect(BridgeErrorCode.MalformedPayload, () => BridgePayloadCodec.DecodeText(trailing));
    }

    private static void VerifyIdentityAndSchema()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures");
        var identityJson = File.ReadAllText(Path.Combine(fixturePath, "identity-v0.json"));
        var vectors = JsonSerializer.Deserialize<List<IdentityVector>>(identityJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidOperationException("Identity vectors are empty");

        foreach (var vector in vectors)
        {
            var kind = Enum.Parse<AssetKind>(vector.Kind);
            var canonical = Convert.FromHexString(vector.CanonicalHex);
            Equal(vector.PreimageHex, Convert.ToHexString(AssetIdentity.CreatePreimage(
                kind, vector.CanonicalFormatVersion, canonical)).ToLowerInvariant(), vector.Name + " preimage");
            var assetId = AssetId.Compute(kind, vector.CanonicalFormatVersion, canonical);
            Equal(vector.AssetId, assetId.ToString(), vector.Name + " hash");
            Equal(assetId, AssetId.Compute(kind, vector.CanonicalFormatVersion, canonical), vector.Name + " repeat");
            Equal(assetId, AssetId.Parse(vector.AssetId), vector.Name + " parse");
        }
        Equal(vectors.Count, vectors.Select(vector => vector.AssetId).Distinct().Count(), "identity domains");

        var projectBytes = File.ReadAllBytes(Path.Combine(fixturePath, "project-v0.json"));
        using var project = ProjectSchema.Parse(projectBytes);
        var entityJson = project.RootElement.GetProperty("projectId").GetRawText();
        var entityId = JsonSerializer.Deserialize<EntityId>(entityJson);
        Equal(entityJson, JsonSerializer.Serialize(entityId), "entity ID serialization");
        var duplicate = entityId.Duplicate();
        Equal(false, entityId == duplicate, "duplicate entity ID");
        Equal('4', duplicate.ToString()[14], "generated entity UUID version");
        try
        {
            JsonSerializer.Serialize(default(EntityId));
            throw new InvalidOperationException("Expected empty entity ID rejection");
        }
        catch (JsonException)
        {
        }

        var futureBytes = System.Text.Encoding.UTF8.GetBytes(
            $"{{\"schemaVersion\":{ProjectSchema.CurrentVersion + 1},\"futureData\":true}}");
        var checksum = SHA256.HashData(futureBytes);
        try
        {
            using var ignored = ProjectSchema.Parse(futureBytes);
            throw new InvalidOperationException("Expected future project schema rejection");
        }
        catch (UnsupportedProjectSchemaException exception) when (exception.IsNewer)
        {
        }
        Equal(true, checksum.SequenceEqual(SHA256.HashData(futureBytes)), "future project remains unchanged");

        try
        {
            using var ignored = ProjectSchema.Parse("{\"schemaVersion\":0,\"schemaVersion\":1}"u8.ToArray());
            throw new InvalidOperationException("Expected duplicate schema version rejection");
        }
        catch (JsonException)
        {
        }
    }

    private static void Expect(BridgeErrorCode code, Action action)
    {
        try
        {
            action();
            throw new InvalidOperationException($"Expected protocol error {code}");
        }
        catch (BridgeProtocolException exception) when (exception.Code == code)
        {
        }
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (expected is BridgeFrame expectedFrame && actual is BridgeFrame actualFrame)
        {
            if (expectedFrame.Kind == actualFrame.Kind &&
                expectedFrame.Opcode == actualFrame.Opcode &&
                expectedFrame.Status == actualFrame.Status &&
                expectedFrame.RequestId == actualFrame.RequestId &&
                expectedFrame.Payload.SequenceEqual(actualFrame.Payload)) return;
        }
        else if (EqualityComparer<T>.Default.Equals(expected, actual))
        {
            return;
        }
        throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    private sealed record GoldenFrame(
        string Name,
        byte Kind,
        ushort Opcode,
        ushort Status,
        uint RequestId,
        string PayloadHex,
        string FrameHex)
    {
        public BridgeFrame ToFrame() => new(
            (BridgeMessageKind)Kind,
            (BridgeOpcode)Opcode,
            (BridgeErrorCode)Status,
            RequestId,
            Convert.FromHexString(PayloadHex));
    }

    private sealed record IdentityVector(
        string Name,
        string Kind,
        uint CanonicalFormatVersion,
        string CanonicalHex,
        string PreimageHex,
        string AssetId);
}

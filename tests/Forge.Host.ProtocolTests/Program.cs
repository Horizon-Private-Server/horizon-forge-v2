using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Forge.Host.Bridge;
using Forge.Host.Domain;

internal static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "bridge-v1.json"));
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
            await EditorRuntimeTests.RunAsync();
            await UyaProjectTests.RunAsync();
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
        BinaryPrimitives.WriteUInt16LittleEndian(badVersion.AsSpan(4), 2);
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

        var futureBytes = "{\"schemaVersion\":2,\"futureData\":true}"u8.ToArray();
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

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Forge.Host.Bridge;
using Forge.Host.Domain;

internal static class Program
{
    public static int Main()
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
        var entityJson = project.RootElement.GetProperty("entities")[0].GetProperty("entityId").GetRawText();
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

        var futureBytes = "{\"schemaVersion\":1,\"futureData\":true}"u8.ToArray();
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

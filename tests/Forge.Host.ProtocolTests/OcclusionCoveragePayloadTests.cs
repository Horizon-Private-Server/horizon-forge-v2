using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Host.Bridge;
using Forge.Host.Domain;

internal static class OcclusionCoveragePayloadTests
{
    internal static void Run()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "occlusion-coverage-v1.json")));
        var root = fixture.RootElement;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
        var expected = root.GetProperty("state").Deserialize<ProjectOcclusionCoverageState>(options)!;
        var request = Convert.FromHexString(root.GetProperty("requestHex").GetString()!);
        var decoded = OcclusionCoveragePayloadCodec.DecodeUpdate(request);
        Check(decoded.Fingerprint == root.GetProperty("projectFingerprint").GetString(), "Cross-language project fingerprint must match.");
        Check(ForgeProjectPersistence.Serialize(decoded.State).SequenceEqual(ForgeProjectPersistence.Serialize(expected)),
            "The TypeScript vector must decode to the complete C# coverage recipe.");
        var response = OcclusionCoveragePayloadCodec.EncodeSnapshot(new(decoded.Fingerprint,
            root.GetProperty("recipeFingerprint").GetString()!, root.GetProperty("visibilityFingerprint").GetString()!, expected));
        Check(Convert.ToHexStringLower(response) == root.GetProperty("snapshotHex").GetString(),
            "C# coverage output must match the TypeScript snapshot vector.");
        for (var i = 0; i < request.Length; i++) Throws<BridgeProtocolException>(() => OcclusionCoveragePayloadCodec.DecodeUpdate(request.AsSpan(0, i)));
        Throws<BridgeProtocolException>(() => OcclusionCoveragePayloadCodec.DecodeUpdate([.. request, 0]));
        var count = request.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(count.AsSpan(73), uint.MaxValue);
        Throws<BridgeProtocolException>(() => OcclusionCoveragePayloadCodec.DecodeUpdate(count));
        var version = request.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(version.AsSpan(68), 2);
        Throws<BridgeProtocolException>(() => OcclusionCoveragePayloadCodec.DecodeUpdate(version));
        var kind = request.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(kind.AsSpan(169), 99);
        Throws<BridgeProtocolException>(() => OcclusionCoveragePayloadCodec.DecodeUpdate(kind));
        var role = request.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(role.AsSpan(125), 99);
        Throws<InvalidDataException>(() => OcclusionCoveragePayloadCodec.DecodeUpdate(role));
        using var stickyFixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "occlusion-coverage-sticky-v1.json")));
        var sticky = stickyFixture.RootElement;
        var stickyState = sticky.GetProperty("state").Deserialize<ProjectOcclusionCoverageState>(options)!;
        var stickyRequest = Convert.FromHexString(sticky.GetProperty("requestHex").GetString()!);
        var stickyDecoded = OcclusionCoveragePayloadCodec.DecodeUpdate(stickyRequest);
        Check(ForgeProjectPersistence.Serialize(stickyDecoded.State).SequenceEqual(ForgeProjectPersistence.Serialize(stickyState)),
            "Extended graph payloads retain Sticky and distance across languages.");
        var stickyResponse = OcclusionCoveragePayloadCodec.EncodeSnapshot(new(decoded.Fingerprint,
            root.GetProperty("recipeFingerprint").GetString()!, root.GetProperty("visibilityFingerprint").GetString()!, stickyState));
        Check(Convert.ToHexStringLower(stickyResponse) == sticky.GetProperty("snapshotHex").GetString(), "Sticky snapshots match the shared vector.");
        var offset = sticky.GetProperty("requestOffsets");
        foreach (var distance in new[] { -1f, float.NaN, float.PositiveInfinity, 1_000_001f })
        {
            var invalid = stickyRequest.ToArray(); BinaryPrimitives.WriteSingleLittleEndian(invalid.AsSpan(offset.GetProperty("stickyDistance").GetInt32()), distance);
            Throws<InvalidDataException>(() => OcclusionCoveragePayloadCodec.DecodeUpdate(invalid));
        }
        var badBoolean = stickyRequest.ToArray(); badBoolean[offset.GetProperty("sticky").GetInt32()] = 2;
        Throws<BridgeProtocolException>(() => OcclusionCoveragePayloadCodec.DecodeUpdate(badBoolean));
        using var volumes = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "occlusion-coverage-volume-graphs-v1.json")));
        var volumeRoot = volumes.RootElement;
        var volumeState = volumeRoot.GetProperty("state").Deserialize<ProjectOcclusionCoverageState>(options)!;
        var volumeBytes = Convert.FromHexString(volumeRoot.GetProperty("requestHex").GetString()!);
        var volumeDecoded = OcclusionCoveragePayloadCodec.DecodeUpdate(volumeBytes);
        Check(ForgeProjectPersistence.Serialize(volumeDecoded.State).SequenceEqual(ForgeProjectPersistence.Serialize(volumeState)),
            "Shape-owned graphs and saved disabled graphs decode identically across languages.");
        var volumeResponse = OcclusionCoveragePayloadCodec.EncodeSnapshot(new(decoded.Fingerprint,
            root.GetProperty("recipeFingerprint").GetString()!, root.GetProperty("visibilityFingerprint").GetString()!, volumeState));
        Check(Convert.ToHexStringLower(volumeResponse) == volumeRoot.GetProperty("snapshotHex").GetString(), "Shape graph snapshots match the shared vector.");
        for (var i = 0; i < volumeBytes.Length; i++) Throws<BridgeProtocolException>(() => OcclusionCoveragePayloadCodec.DecodeUpdate(volumeBytes.AsSpan(0, i)));
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}

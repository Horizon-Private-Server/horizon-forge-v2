using System.Text.Json;
using System.Buffers.Binary;
using Forge.Host.Bridge;
using Forge.Host.Domain;

internal static class OcclusionCoveragePreviewPayloadTests
{
    internal static void Run()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "occlusion-coverage-preview-v1.json")));
        var fixture = document.RootElement; var preview = fixture.GetProperty("preview");
        var request = Convert.FromHexString(fixture.GetProperty("requestHex").GetString()!);
        var decoded = OcclusionCoveragePreviewPayloadCodec.DecodeRequest(request);
        Check(decoded.Fingerprint == new string('a', 64) && decoded.SourcePath == "", "Preview request must match the TypeScript fixture.");
        for (var size = 0; size < request.Length; size++)
            Throws(() => OcclusionCoveragePreviewPayloadCodec.DecodeRequest(request.AsSpan(0, size)));
        Throws(() => OcclusionCoveragePreviewPayloadCodec.DecodeRequest([.. request, 0]));
        foreach (var path in new[] { "\0", new string('x', 4097) })
        {
            var invalid = new PayloadWriter(); invalid.WriteString(new string('a', 64)); invalid.WriteString(path);
            Throws(() => OcclusionCoveragePreviewPayloadCodec.DecodeRequest(invalid.ToArray()));
        }
        using var graphDocument = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "occlusion-coverage-preview-graph-v1.json")));
        var graphFixture = graphDocument.RootElement;
        var graphEvaluation = graphFixture.GetProperty("preview").Deserialize<EditorOcclusionCoverageEvaluation>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var graphEncoded = OcclusionCoveragePreviewPayloadCodec.Encode(new(new string('a', 64), new string('b', 64), graphEvaluation));
        Check(Convert.ToHexStringLower(graphEncoded) == graphFixture.GetProperty("resultHex").GetString(), "Graph results must match the TypeScript extension fixture.");
        var graph = graphEvaluation.GraphPreview!;
        var boundary = OcclusionCoveragePreviewPayloadCodec.Encode(new(new string('a', 64), new string('b', 64),
            graphEvaluation with { GraphPreview = graph with { QueryTests = 200_000_000 } }));
        var queryOffset = graphFixture.GetProperty("offsets").GetProperty("queryTests").GetInt32();
        Check(boundary.Length == graphEncoded.Length && BinaryPrimitives.ReadUInt32LittleEndian(boundary.AsSpan(queryOffset)) == 200_000_000,
            "The calibrated graph query budget retains the existing wire layout.");
        foreach (var invalid in new[] { graph with { Rays = 0 }, graph with { QueryTests = 200_000_001 }, graph with { SceneFingerprint = "invalid" },
            graph with { Sources = [] }, graph with { Sources = [graph.Sources[0] with { Rejected = graph.Sources[0].Accepted }] },
            graph with { Sources = [graph.Sources[0], graph.Sources[0]] }, graph with { Sources = [graph.Sources[0] with { Id = EntityId.New() }] } })
            Throws(() => OcclusionCoveragePreviewPayloadCodec.Encode(new(new string('a', 64), new string('b', 64), graphEvaluation with { GraphPreview = invalid })));
        Throws(() => OcclusionCoveragePreviewPayloadCodec.Encode(new(new string('a', 64), new string('b', 64), graphEvaluation with { GraphPreview = null })));
        request[4] = (byte)'A'; Throws(() => OcclusionCoveragePreviewPayloadCodec.DecodeRequest(request));
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var evaluation = preview.Deserialize<EditorOcclusionCoverageEvaluation>(options)!;
        var encoded = OcclusionCoveragePreviewPayloadCodec.Encode(new(new string('a', 64), new string('b', 64), evaluation));
        Check(Convert.ToHexStringLower(encoded) == fixture.GetProperty("resultHex").GetString(), "Preview results must match the shared TypeScript fixture.");
    }
    private static void Throws(Action action)
    {
        try { action(); } catch (BridgeProtocolException) { return; } throw new Exception("Malformed preview payload was accepted.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}

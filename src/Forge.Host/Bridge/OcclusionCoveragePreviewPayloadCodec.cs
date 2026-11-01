using Forge.Host.Domain;

namespace Forge.Host.Bridge;

internal static class OcclusionCoveragePreviewPayloadCodec
{
    internal static (string Fingerprint, string SourcePath) DecodeRequest(ReadOnlySpan<byte> bytes)
    {
        var reader = new PayloadReader(bytes);
        var fingerprint = reader.ReadString(); var path = reader.ReadString(); reader.Complete();
        if (!BakeSchema.IsFingerprint(fingerprint) || fingerprint.Any(c => c is >= 'A' and <= 'F')
            || path.Length > 4096 || path.Contains('\0'))
            PayloadFormat.Malformed("Coverage preview request is invalid.");
        return (fingerprint, path);
    }

    internal static byte[] Encode(EditorOcclusionCoveragePreview preview)
    {
        var writer = new PayloadWriter(); var result = preview.Evaluation;
        writer.WriteString(preview.ProjectFingerprint); writer.WriteString(preview.RecipeFingerprint);
        writer.WriteUInt32(checked((uint)result.AlgorithmVersion)); writer.WriteSingle(result.CellSize);
        writer.WriteString(result.PrimaryFingerprint); writer.WriteString(result.CoordinateFingerprint);
        writer.WriteUInt32(checked((uint)result.PrimaryCells)); writer.WriteUInt32(checked((uint)result.CandidateTests));
        writer.WriteUInt32(checked((uint)result.Sources.Count));
        foreach (var source in result.Sources) { writer.WriteString(source.Id.ToString()); writer.WriteUInt32(checked((uint)source.Cells)); }
        writer.WriteUInt32(checked((uint)result.Cells.Count));
        foreach (var cell in result.Cells)
        {
            writer.WriteUInt32(checked((uint)cell.X)); writer.WriteUInt32(checked((uint)cell.Y)); writer.WriteUInt32(checked((uint)cell.Z));
        }
        if (result.GraphPreview is { } graph)
        {
            ValidateGraph(result, graph);
            writer.WriteUInt32(1); writer.WriteString(graph.SceneFingerprint);
            writer.WriteUInt32((uint)graph.Rays); writer.WriteUInt32((uint)graph.QueryTests); writer.WriteUInt32((uint)graph.Sources.Count);
            foreach (var source in graph.Sources)
            {
                writer.WriteString(source.Id.ToString());
                foreach (var cells in new[] { source.Accepted, source.Rejected })
                {
                    writer.WriteUInt32((uint)cells.Count);
                    foreach (var c in cells) { writer.WriteUInt32((uint)c.X); writer.WriteUInt32((uint)c.Y); writer.WriteUInt32((uint)c.Z); }
                }
            }
        }
        else if (result.AlgorithmVersion == 3) PayloadFormat.Malformed("Graph preview scene identity is missing.");
        return writer.ToArray();
    }
    private static void ValidateGraph(EditorOcclusionCoverageEvaluation result, EditorOcclusionCoverageGraphPreview graph)
    {
        if (result.AlgorithmVersion != 3 || !BakeSchema.IsFingerprint(graph.SceneFingerprint)
            || graph.SceneFingerprint.Any(c => c is >= 'A' and <= 'F') || graph.Rays is < 1 or > 2_000_000
            || graph.QueryTests is < 0 or > 200_000_000 || graph.Sources.Count is < 1 or > 1024)
            PayloadFormat.Malformed("Invalid graph preview profile or identity.");
        var ids = new HashSet<EntityId>(); var total = 0;
        foreach (var source in graph.Sources)
        {
            var contribution = result.Sources.SingleOrDefault(s => s.Id == source.Id);
            if (contribution is null || !ids.Add(source.Id) || contribution.Cells != source.Accepted.Count)
                PayloadFormat.Malformed("Invalid graph preview ownership or contribution count.");
            var seen = new HashSet<EditorOcclusionCoverageCell>();
            foreach (var cells in new[] { source.Accepted, source.Rejected })
            {
                if (cells.Count > 100_000 - total) PayloadFormat.Malformed("Graph inspection exceeds its cell limit.");
                total += cells.Count; EditorOcclusionCoverageCell? previous = null;
                foreach (var c in cells)
                {
                    if (c.X < 0 || c.Y < 0 || c.Z < 0 || !seen.Add(c)
                        || previous is { } p && (p.Z > c.Z || p.Z == c.Z && (p.Y > c.Y || p.Y == c.Y && p.X >= c.X)))
                        PayloadFormat.Malformed("Invalid graph preview coordinates or ordering.");
                    previous = c;
                }
            }
        }
        if (result.CandidateTests < total) PayloadFormat.Malformed("Invalid graph candidate counts.");
    }
}

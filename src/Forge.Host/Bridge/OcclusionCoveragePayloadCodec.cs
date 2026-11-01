using Forge.Host.Domain;
using Forge.Host.Domain.Occlusion;

namespace Forge.Host.Bridge;

internal static class OcclusionCoveragePayloadCodec
{
    internal static (string Fingerprint, ProjectOcclusionCoverageState State) DecodeUpdate(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var fingerprint = reader.ReadString();
        if (!BakeSchema.IsFingerprint(fingerprint) || fingerprint.Any(c => c is >= 'A' and <= 'F'))
            PayloadFormat.Malformed("Occlusion coverage project fingerprint is invalid.");
        var version = reader.ReadUInt32();
        if (version != ProjectOcclusionCoverageSchema.CurrentVersion) PayloadFormat.Malformed("Unsupported occlusion coverage schema.");
        var primary = reader.ReadBoolean();
        var count = reader.ReadUInt32();
        if (count > ProjectOcclusionCoverageSchema.MaximumSources) PayloadFormat.Malformed("Occlusion source count exceeds limit.");
        var sources = new ProjectOcclusionCoverageSource[count];
        var totalMarkers = 0L;
        for (var i = 0; i < sources.Length; i++)
        {
            var id = EntityId.Parse(reader.ReadString()); var name = reader.ReadString();
            var enabled = reader.ReadBoolean(); var role = (ProjectOcclusionCoverageRole)reader.ReadUInt32();
            var transform = new ProjectTransform(ReadVector(ref reader),
                new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), ReadVector(ref reader));
            var kind = reader.ReadUInt32();
            ProjectVector3? box = null;
            ProjectOcclusionCoverageCylinder? cylinder = null;
            ProjectOcclusionCoverageGraph? graph = null;
            if (kind is 1 or 5) box = ReadVector(ref reader);
            else if (kind is 2 or 6) cylinder = new(reader.ReadSingle(), reader.ReadSingle());
            else if (kind is not (3 or 4)) PayloadFormat.Malformed("Unknown occlusion source kind.");
            if (kind is >= 3 and <= 6)
            {
                var size = kind is 3 or 4 ? ReadVector(ref reader) : null;
                var disabled = kind >= 5 && reader.ReadBoolean();
                var sticky = kind != 3 && reader.ReadBoolean();
                float? stickyDistance = kind != 3 ? reader.ReadSingle() : null;
                var markerCount = reader.ReadUInt32();
                totalMarkers += markerCount;
                if (markerCount > ProjectOcclusionCoverageSchema.MaximumMarkersPerGraph
                    || totalMarkers > ProjectOcclusionCoverageSchema.MaximumMarkers)
                    PayloadFormat.Malformed("Occlusion marker count exceeds limit.");
                var markers = new ProjectOcclusionCoverageMarker[markerCount];
                for (var j = 0; j < markers.Length; j++)
                    markers[j] = new(EntityId.Parse(reader.ReadString()), reader.ReadString(), ReadVector(ref reader));
                graph = new(size, markers, sticky, stickyDistance, disabled);
            }
            sources[i] = new(id, name, enabled, role, transform, box, cylinder, graph);
        }
        reader.Complete();
        var state = new ProjectOcclusionCoverageState((int)version, primary, sources);
        OcclusionCoverageRecipe.Validate(state);
        return (fingerprint, state);
    }

    internal static byte[] EncodeSnapshot(EditorOcclusionCoverageSnapshot snapshot)
    {
        var writer = new PayloadWriter();
        writer.WriteString(snapshot.ProjectFingerprint); writer.WriteString(snapshot.RecipeFingerprint);
        writer.WriteString(snapshot.VisibilityFingerprint);
        var state = snapshot.State;
        OcclusionCoverageRecipe.Validate(state);
        writer.WriteUInt32((uint)state.SchemaVersion); writer.WriteBoolean(state.PrimaryEnabled);
        writer.WriteUInt32((uint)state.Sources.Count);
        foreach (var source in state.Sources)
        {
            writer.WriteString(source.Id.ToString()); writer.WriteString(source.Name);
            writer.WriteBoolean(source.Enabled); writer.WriteUInt32((uint)source.Role);
            WriteVector(writer, source.Transform.Position);
            var q = source.Transform.Rotation;
            writer.WriteSingle(q.X); writer.WriteSingle(q.Y); writer.WriteSingle(q.Z); writer.WriteSingle(q.W);
            WriteVector(writer, source.Transform.Scale);
            var attached = source.Graph is not null && (source.BoxSize is not null || source.Cylinder is not null);
            var extendedGraph = source.Graph is { } g && (g.Sticky || g.StickyDistance.HasValue);
            var kind = attached ? source.BoxSize is not null ? 5u : 6u : source.BoxSize is not null ? 1u : source.Cylinder is not null ? 2u : extendedGraph ? 4u : 3u;
            writer.WriteUInt32(kind);
            if (source.BoxSize is { } box) WriteVector(writer, box);
            else if (source.Cylinder is { } cylinder) { writer.WriteSingle(cylinder.Radius); writer.WriteSingle(cylinder.Height); }
            if (source.Graph is { } graph)
            {
                if (kind is 3 or 4) WriteVector(writer, graph.Size!);
                if (attached) writer.WriteBoolean(graph.Disabled);
                if (kind != 3) { writer.WriteBoolean(graph.Sticky); writer.WriteSingle(graph.StickyDistance ?? 12); }
                writer.WriteUInt32((uint)graph.Markers.Count);
                foreach (var marker in graph.Markers)
                {
                    writer.WriteString(marker.Id.ToString()); writer.WriteString(marker.Name); WriteVector(writer, marker.Position);
                }
            }
        }
        return writer.ToArray();
    }

    internal static byte[] EncodeUpdateResult(EditorOcclusionCoverageUpdateResult result)
    {
        var writer = new PayloadWriter();
        writer.WriteBytes(EditorPayloadCodec.EncodeSnapshot(result.Project), BridgeFrameCodec.MaxPayloadLength - 8);
        writer.WriteBytes(EncodeSnapshot(result.Coverage), BridgeFrameCodec.MaxPayloadLength - 8);
        return writer.ToArray();
    }

    private static ProjectVector3 ReadVector(ref PayloadReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    private static void WriteVector(PayloadWriter writer, ProjectVector3 value)
    {
        writer.WriteSingle(value.X); writer.WriteSingle(value.Y); writer.WriteSingle(value.Z);
    }
}

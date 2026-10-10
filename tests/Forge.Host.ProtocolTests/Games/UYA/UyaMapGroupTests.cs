using System.Buffers.Binary;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaMapGroupTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-map-groups-{Guid.NewGuid():N}");
        try
        {
            var projectPath = Path.Combine(root, "project");
            var moby = Entity("Moby", "mobys", "gameplay/core/moby_instances", 0);
            var tie = Entity("Tie", "ties", "gameplay/core/tie_instances", 0);
            var tieCopy = new ProjectEntity(
                EntityId.New(), "Tie copy", "ties", ProjectTransform.Identity, null,
                Source: new(0, [0], SourceIndex: 0));
            var shrub = Entity("Shrub", "shrubs", "gameplay/core/shrub_instances", 0);
            var workspace = await ForgeProjectWorkspace.CreateAsync(
                projectPath,
                "Map groups",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 40, new string('a', 32)),
                [moby, tie, tieCopy, shrub]);
            var custom = workspace.CreateGroup("Forge-only group");
            workspace.AddGroupMembers(custom.GroupId, [moby.EntityId]);
            await workspace.SaveAsync();

            var captures = new[]
            {
                Capture("gameplay/moby_groups", 0x50, GroupBytes([0])),
                Capture("gameplay/tie_groups", 0x38, GroupBytes([0])),
                Capture("gameplay/shrub_groups", 0x44, GroupBytes([0, 3])),
            };
            await OpaqueContentStore.WriteAsync(
                projectPath,
                new("UYA", "NTSC-U", "1.00", 40, new string('b', 32)),
                captures);

            await using var runtime = new EditorRuntime(mapGroupResolver: UyaMapGroupService.ReadAsync);
            var snapshot = await runtime.OpenAsync(projectPath, TimeSpan.Zero);
            Equal([moby.EntityId], snapshot.MapGroups.Single(group => group.Kind == EditorMapGroupKind.Moby).Members,
                "moby map group members");
            Equal([tie.EntityId, tieCopy.EntityId],
                snapshot.MapGroups.Single(group => group.Kind == EditorMapGroupKind.Tie).Members,
                "copied tie keeps its baked source-group membership");
            var shrubGroup = snapshot.MapGroups.Single(group => group.Kind == EditorMapGroupKind.Shrub);
            Equal([shrub.EntityId], shrubGroup.Members, "shrub map group members");
            Equal([3], shrubGroup.MissingSourceIndices, "unresolved source member remains visible");
            Equal(["Forge-only group"], snapshot.Groups.Select(group => group.Name),
                "custom groups remain separate from map groups");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static ProjectEntity Entity(string name, string layer, string section, int sourceIndex) =>
        new(EntityId.New(), name, layer, ProjectTransform.Identity, null,
            new("UYA", 40, section, sourceIndex), Source: new(0, [0], SourceIndex: sourceIndex));

    private static OpaqueSectionCapture Capture(string name, int headerOffset, byte[] bytes) =>
        new(name, new("gameplay/gameplay_core.bin", headerOffset, headerOffset, bytes.Length, 1), bytes);

    private static byte[] GroupBytes(params int[][] groups)
    {
        var dataOffset = (0x10 + groups.Length * sizeof(int) + 0xf) & ~0xf;
        var memberBytes = groups.Sum(group => group.Length * sizeof(ushort));
        var dataSize = (memberBytes + 0xf) & ~0xf;
        var bytes = new byte[dataOffset + dataSize];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, groups.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), dataSize);
        var offset = dataOffset;
        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var group = groups[groupIndex];
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x10 + groupIndex * sizeof(int)),
                group.Length == 0 ? -1 : offset - dataOffset);
            for (var memberIndex = 0; memberIndex < group.Length; memberIndex++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset),
                    (ushort)(group[memberIndex] | (memberIndex == group.Length - 1 ? 0x8000 : 0)));
                offset += sizeof(ushort);
            }
        }
        return bytes;
    }

    private static void Equal<T>(IEnumerable<T> expected, IEnumerable<T> actual, string context)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException(
                $"{context}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
    }
}

using System.Buffers.Binary;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaMapGroupContentImportTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-map-group-import-{Guid.NewGuid():N}");
        try
        {
            var first = Tie("First", 0, 5);
            var second = Tie("Second", 1, 2);
            var workspace = await ForgeProjectWorkspace.CreateAsync(
                root,
                "Map-group import",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 41, new string('a', 32)),
                [first, second]);
            var family = new ContentImportFamilyPlan(
                "mapGroups", "Map groups", ContentImportMode.Replace,
                ContentImportMode.Replace, ContentImportSourceState.Present,
                true, false, true,
                ["source:mapGroups:tie:table", "source:mapGroups:tie:0"],
                [], [], ["mapGroups:project"], [], [], [], 0x20, [], []);
            var plan = new ContentImportPlan(
                ContentImportPlanSchema.CurrentVersion,
                ContentImportCapabilitySchema.CurrentVersion,
                $"import-{new string('b', 32)}",
                "UYA", "NTSC-U", "1.00",
                new string('c', 32), new string('d', 64), 40,
                workspace.Manifest.ProjectId.ToString(), workspace.CurrentFingerprint, 41,
                0x20, [family], [], []);
            var command = UyaMapGroupContentImportService.PrepareCommit(
                workspace,
                [first, second],
                [Capture("gameplay/tie_groups", GroupBytes([0]))],
                plan,
                1234);

            await workspace.CommitContentImportAsync(command);
            await workspace.SaveAsync();
            workspace = await ForgeProjectWorkspace.OpenAsync(root);
            var table = workspace.Content.MapGroups.Single();
            Equal(ProjectEntityKind.Tie, table.MemberKind, "member kind survives reopen");
            SequenceEqual([first.EntityId], table.Groups.Single().Members, "stable group membership survives reopen");

            var bytes = UyaStaticReferenceStore.PrepareProjectGroups(workspace, BakeLayerId.Ties)
                ?? throw new InvalidOperationException("Expected an authoritative TIE group table.");
            SequenceEqual([1], UyaTieGroupsReader.Read(bytes).Groups.Single(),
                "bake remaps stable IDs after final class ordering");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static ProjectEntity Tie(string name, int sourceIndex, int classId) => new(
        EntityId.New(), name, "ties", ProjectTransform.Identity, null,
        new("UYA", 40, "gameplay/core/tie_instances", sourceIndex),
        Source: new(classId, [0], SourceIndex: sourceIndex));

    private static OpaqueSectionCapture Capture(string name, byte[] bytes) =>
        new(name, new("gameplay/gameplay_core.bin", 0x38, 0x38, bytes.Length, 1), bytes);

    private static byte[] GroupBytes(params int[][] groups)
    {
        var dataOffset = (0x10 + groups.Length * sizeof(int) + 0xf) & ~0xf;
        var bytes = new byte[dataOffset + ((groups.Sum(value => value.Length * sizeof(ushort)) + 0xf) & ~0xf)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, groups.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - dataOffset);
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

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string context)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException(
                $"{context}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
    }
}

using System.Buffers.Binary;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaOcclusionContentImportTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-occlusion-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var workspace = await ForgeProjectWorkspace.CreateAsync(
                Path.Combine(root, "project"),
                "Occlusion import",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 41, new string('a', 32)),
                []);
            var source = new UyaOcclusionImportSource(new byte[0x10], Grid(), [1, 2, 3, 4]);
            var family = new ContentImportFamilyPlan(
                "occlusion", "Occlusion", ContentImportMode.Replace,
                ContentImportMode.Replace, ContentImportSourceState.Present,
                true, false, true,
                ["source:occlusion:singleton"], [], [], ["occlusion:project"], [],
                ["occlusion requires complete static-family replacement"], [],
                0x100, [], []);
            var plan = new ContentImportPlan(
                ContentImportPlanSchema.CurrentVersion,
                ContentImportCapabilitySchema.CurrentVersion,
                $"import-{new string('b', 32)}",
                "UYA", "NTSC-U", "1.00", new string('c', 32), new string('d', 64), 5,
                workspace.Manifest.ProjectId.ToString(), workspace.CurrentFingerprint, 41,
                0x100, [family], [], []);

            var command = UyaOcclusionContentImportService.PrepareCommit(workspace, source, plan, 1234);
            Equal(1, command.Attachments.Count, "occlusion payload attachment");
            Equal(true, command.Batch.Replaced.SequenceEqual(["occlusion:project"]),
                "occlusion replacement ledger");
            await workspace.CommitContentImportAsync(command);
            await workspace.SaveAsync();

            workspace = await ForgeProjectWorkspace.OpenAsync(workspace.RootPath);
            Equal(5, workspace.Content.Occlusion!.SourceLevel, "donor level survives reopen");
            var decoded = await UyaOcclusionImportPayload.ReadAsync(workspace, CancellationToken.None)
                ?? throw new InvalidOperationException("Imported occlusion payload is missing.");
            Equal(true, decoded.GameplayMappings.SequenceEqual(source.GameplayMappings),
                "gameplay mappings remain exact");
            Equal(true, decoded.AssetGrid.SequenceEqual(source.AssetGrid),
                "asset grid remains exact");
            Equal(true, decoded.LevelOcclusion.SequenceEqual(source.LevelOcclusion),
                "level payload remains exact");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] Grid()
    {
        var bytes = new byte[0xb0];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 0x30);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 5);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
        return bytes;
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}

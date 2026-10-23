using System.Buffers.Binary;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Hud;
using RatchetPs2.Core.Textures.Pif;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaHudContentImportTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-hud-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            var targetFingerprint = new string('a', 32);
            var donorFingerprint = new string('b', 32);
            var (header, bank, textureBytes, donorHud) = BuildHud();
            var texture = await catalog.PutAsync(
                AssetKind.Texture,
                ProjectTextureAssetSchema.CanonicalFormatVersion,
                textureBytes,
                new(
                    "donor-hud",
                    new("UYA", "NTSC-U", "1.00", "level40", "level_wad/level_data.wad", 0,
                        donorFingerprint),
                    ["hud:0123:frame:0"],
                    ["vanilla", "game:UYA", "level:40", "hud"]));
            donorHud = donorHud with
            {
                SourceIcons = donorHud.SourceIcons.Select(icon => icon with
                {
                    Frames = icon.Frames.Select(frame => frame with
                    {
                        Texture = new(texture.Id, AssetKind.Texture),
                    }).ToArray(),
                }).ToArray(),
            };

            var projectPath = Path.Combine(root, "project");
            var workspace = await ForgeProjectWorkspace.CreateAsync(
                projectPath,
                "HUD import",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 41, targetFingerprint),
                []);
            workspace = workspace.CreateProjection(workspace.Content with
            {
                Hud = new(ProjectHudSchema.CurrentVersion, 0, [], []),
            });
            var family = new ContentImportFamilyPlan(
                "hud", "HUD", ContentImportMode.Replace,
                ContentImportMode.Replace, ContentImportSourceState.Present,
                true, false, true,
                ["source:hud:singleton"], [], [], ["hud:project"], [],
                ["hud requires the complete header and physical bank set."], [],
                header.Length + bank.Length, [], []);
            var plan = new ContentImportPlan(
                ContentImportPlanSchema.CurrentVersion,
                ContentImportCapabilitySchema.CurrentVersion,
                $"import-{new string('c', 32)}",
                "UYA",
                "NTSC-U",
                "1.00",
                donorFingerprint,
                new string('d', 64),
                40,
                workspace.Manifest.ProjectId.ToString(),
                workspace.CurrentFingerprint,
                41,
                header.Length + bank.Length,
                [family],
                [],
                []);
            var source = new UyaHudContentImportSource(
                donorHud,
                [
                    Capture("level-data/hud-header", header),
                    Capture("level-data/hud-bank-0", bank),
                ]);
            var command = await UyaHudContentImportService.PrepareCommitAsync(
                workspace, catalog, source, plan, 1234);
            Equal(2, command.Attachments.Count, "HUD payload and preview texture are attached");
            Equal(true, command.Batch.Replaced.SequenceEqual(["hud:project"]),
                "target HUD is recorded as replaced");

            await workspace.CommitContentImportAsync(command);
            await workspace.SaveAsync();
            workspace = await ForgeProjectWorkspace.OpenAsync(projectPath);
            Equal(texture.Id, workspace.Content.Hud!.SourceIcons.Single().Frames.Single().Texture!.Id,
                "donor HUD texture identity survives reopen");
            var sourcePayload = workspace.Content.Hud.SourcePayload
                ?? throw new InvalidOperationException("Imported HUD source payload is missing.");
            var repeatPlan = plan with
            {
                BatchId = $"import-{new string('e', 32)}",
                TargetFingerprint = workspace.CurrentFingerprint,
            };
            var repeat = await UyaHudContentImportService.PrepareCommitAsync(
                workspace, catalog, source, repeatPlan, 2345);
            Equal(0, repeat.Attachments.Count,
                "reimport does not restage project-owned HUD payloads or textures");
            Equal(2, repeat.Batch.Reused.Count,
                "reimport records the HUD payload and texture as reused");
            var payloadBytes = await workspace.ReadAttachedAssetVerifiedAsync(
                sourcePayload, ForgeProjectWorkspace.MaxAttachedAssetBytes, CancellationToken.None);
            var decoded = UyaHudImportPayload.Decode(payloadBytes);
            Equal(true, decoded.Header.SequenceEqual(header), "attached HUD header remains exact");
            Equal(true, decoded.Banks[0].SequenceEqual(bank), "attached HUD bank remains exact");
            var input = await UyaHudBakeService.CreateBakeInputAsync(workspace, CancellationToken.None);
            Equal(0, input.Blockers?.Count ?? 0, "imported HUD bakes without target opaque HUD data");
            Equal(true, input.AssetIds.Contains(sourcePayload.Id), "HUD bake tracks its portable source payload");
            var staging = await BakeStagingStore.OpenAsync(projectPath);
            var snapshot = await UyaHudBakeService.StageAsync(
                projectPath,
                staging,
                new(
                    BakeLayerId.Hud,
                    BakeLayerState.Dirty,
                    new string('e', 64),
                    new string('f', 64),
                    [],
                    []));
            var replacements = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
            await UyaHudBakeService.AddReplacementsAsync(
                Path.Combine(staging.RootPath, snapshot.RelativePath), replacements, CancellationToken.None);
            Equal(6, replacements.Count, "imported HUD replaces its header and all five banks");
            Equal(true, replacements["hud/header.bin"].Span.SequenceEqual(header),
                "staged replacement keeps the donor HUD header exact");
            Equal(true, replacements["hud/bank0.bin"].Span.SequenceEqual(bank),
                "staged replacement keeps the donor HUD bank exact");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (byte[] Header, byte[] Bank, byte[] Texture, ProjectHudState State) BuildHud()
    {
        var header = new byte[0xd4];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x00), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x02), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x04), 0xb4);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x08), 0xc0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x0c), 0xc4);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x10), 0xcc);
        for (var index = 0; index < ProjectHudSchema.PhysicalBankCount; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x14 + index * 4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x34 + index * 4), 1);
        }
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x54), 0x440);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0xb4), 0x0123);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0xb6), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0xb8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0xbc), HudBankReader.IconMappingTerminator);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0xc4), 0x80000000);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0xcc), 0x80000400);
        header[0xd2] = 3;
        header[0xd3] = 3;
        var bank = new byte[0x440];
        bank[0] = 240;
        bank[1] = 80;
        bank[2] = 30;
        bank[3] = 128;
        var texture = PifWriter.Write(PifWriter.CreateIndexed8(8, 8, bank[..0x400], bank[0x400..]));
        var state = new ProjectHudState(
            ProjectHudSchema.CurrentVersion,
            0,
            [new(0, 0x0123, [new(0, 0, 0, 0, 0, 8, 8, null)])],
            []);
        return (header, bank, texture, state);
    }

    private static OpaqueSectionCapture Capture(string name, byte[] bytes) =>
        new(name, new("level_wad/level_data.wad", 0, 0, bytes.Length, 1), bytes);

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}

using System.Buffers.Binary;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

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
            var terrain = UyaTfragProjectTests.SyntheticTerrain();
            BinaryPrimitives.WriteInt16LittleEndian(terrain.AsSpan(0x40 + 0x3a), 7);
            await UyaTfragProjectService.ApplyAsync(workspace, UyaTfragProjectService.Create(
                [new(BakeLayerId.Tfrags, "primary.bin", AssetKind.Tfrag,
                    "level_wad/assets/asset_wad.bin", 0, terrain)], 41));
            var retainedTie = new ProjectEntity(EntityId.New(), "Retained tie", "ties",
                ProjectTransform.Identity, null, new("UYA", 41, "gameplay/core/tie_instances", 0),
                new(Locked: true), Source: new(1, UyaGameplayInstanceTemplates.CreateTie(1, 0)));
            workspace.AddEntity(retainedTie);
            var entitiesBefore = ForgeProjectPersistence.Serialize(workspace.Content.Entities);
            var terrainBefore = ForgeProjectPersistence.Serialize(workspace.Content.Tfrags);
            await OpaqueContentStore.WriteAsync(workspace.RootPath,
                new("UYA", "NTSC-U", "1.00", 41, new string('a', 32)), []);
            var source = new UyaOcclusionImportSource(new byte[0x10], Grid(), [1, 2, 3, 4]);
            var family = new ContentImportFamilyPlan(
                "occlusion", "Occlusion", ContentImportMode.Replace,
                ContentImportMode.Replace, ContentImportSourceState.Present,
                true, false, true,
                ["source:occlusion:singleton"], [], [], ["occlusion:project"], [],
                [], [],
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
            Equal(true, entitiesBefore.SequenceEqual(ForgeProjectPersistence.Serialize(workspace.Content.Entities)),
                "standalone occlusion leaves retained entities and protection unchanged");
            Equal(true, terrainBefore.SequenceEqual(ForgeProjectPersistence.Serialize(workspace.Content.Tfrags)),
                "standalone occlusion leaves the target terrain inventory unchanged");
            var references = await UyaStaticReferenceStore.PrepareAsync(
                workspace.RootPath, workspace, BakeLayerId.Ties, CancellationToken.None);
            Equal(1, UyaOcclusionMappingsReader.Read(references!.Occlusion!).Ties.Count,
                "independent occlusion rebuilds references for retained TIEs without importing donor TIEs");
            using var iso = new MemoryStream(UyaProjectTests.CreateIso(), writable: false);
            var package = UyaLevelWadUnpacker.Unpack(LevelArchiveReader.ExtractPrimary(GameId.UYA, iso, 3));
            var packed = await UyaTfragPackService.ComposeAsync(workspace, package, root,
                terrain, terrain, new Dictionary<int, IReadOnlyDictionary<int, string>>(), [], null,
                CancellationToken.None);
            Equal(true, packed.TerrainBytes.SequenceEqual(terrain),
                "independent occlusion preserves terrain from another level without rebinding its slots");
            Equal<byte[]?>(null, packed.OcclusionBytes,
                "independent occlusion does not rewrite donor mappings from unrelated terrain");

            var donorTerrain = UyaTfragProjectTests.SyntheticTerrain(1);
            UyaBaseLayerPayload donorPayload = new(BakeLayerId.Tfrags, "primary.bin", AssetKind.Tfrag,
                "level_wad/assets/asset_wad.bin", 0, donorTerrain);
            var terrainFamily = family with
            {
                Family = "tfrags", Mode = ContentImportMode.Add, SupportedMode = ContentImportMode.AddAndReplace,
                Add = ["source:tfrags:primary:0"], Remove = [],
            };
            var terrainPlan = plan with
            {
                BatchId = $"import-{new string('e', 32)}", TargetFingerprint = workspace.CurrentFingerprint,
                Families = [terrainFamily],
            };
            await workspace.CommitContentImportAsync(UyaTfragContentImportService.PrepareCommit(workspace,
                new(UyaTfragProjectService.Create([donorPayload], 5), [donorPayload], []), terrainPlan, 2345));
            var mixedTerrain = TfragTerrainComposer.AppendNativeTerrain(GameId.UYA, terrain, donorTerrain);
            packed = await UyaTfragPackService.ComposeAsync(workspace, package, root,
                mixedTerrain, terrain, new Dictionary<int, IReadOnlyDictionary<int, string>>(), [], null,
                CancellationToken.None);
            Equal(true, packed.TerrainBytes.SequenceEqual(mixedTerrain),
                "occlusion accepts mixed native and donor terrain without forcing replacement");
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

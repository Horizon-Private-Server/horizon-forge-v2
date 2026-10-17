using System.Buffers.Binary;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Textures.Palettes;
using RatchetPs2.Core.Ties;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaCustomTieClassTests
{
    public static async Task RunAsync()
    {
        VerifyAllocationAndDonorRebase();
        VerifyBoundaryAndExhaustion();
        VerifyNativeBoundary();
        await VerifyProjectRoundTripAsync();
    }

    private static void VerifyAllocationAndDonorRebase()
    {
        var first = Id('a');
        var second = Id('b');
        var third = Id('c');
        var registry = UyaCustomTieClassRegistry.AllocateImported(
            ProjectCustomClassRegistry.Empty, first, new HashSet<int>());
        Equal(UyaCustomTieClassRegistry.FirstClassId, registry.Allocations.Single().ClassId,
            "custom TIE allocation starts at the namespace boundary");
        Equal(registry, UyaCustomTieClassRegistry.AllocateImported(registry, first, new HashSet<int>()),
            "identical content reuses its allocation");

        var donor = new[]
        {
            new UyaDonorTieClass(0x9002, third),
            new UyaDonorTieClass(0x9001, first),
            new UyaDonorTieClass(0x9000, second),
            new UyaDonorTieClass(0x9003, second),
        };
        var rebased = UyaCustomTieClassRegistry.RebaseDonor(registry, donor, new HashSet<int>());
        Equal(0x9000, Target(rebased, first), "identical donor content reuses the existing target");
        Equal(0x9001, Target(rebased, second), "conflicting donor slot remaps to the first free target");
        Equal(0x9002, Target(rebased, third), "free donor slot is preserved");
        Equal(0x9001, rebased.Remaps.Single(value => value.SourceClassId == 0x9003).TargetClassId,
            "identical donor content shares one target allocation");
        var reordered = UyaCustomTieClassRegistry.RebaseDonor(
            registry, donor.Reverse().ToArray(), new HashSet<int>());
        Equal(true, rebased.Registry.Allocations.SequenceEqual(reordered.Registry.Allocations),
            "donor allocation is input-order deterministic");
        Equal(true, rebased.Remaps.SequenceEqual(reordered.Remaps),
            "donor remap is input-order deterministic");

        Throws<InvalidDataException>(() => UyaCustomTieClassRegistry.RebaseDonor(
            registry,
            [new(0x9004, second), new(0x9004, third)],
            new HashSet<int>()), "duplicate donor slot with different content");
    }

    private static void VerifyBoundaryAndExhaustion()
    {
        var allButLast = Enumerable.Range(
            UyaCustomTieClassRegistry.FirstClassId,
            UyaCustomTieClassRegistry.LastClassId - UyaCustomTieClassRegistry.FirstClassId)
            .ToHashSet();
        var boundary = UyaCustomTieClassRegistry.AllocateImported(
            ProjectCustomClassRegistry.Empty, Id('d'), allButLast);
        Equal(0xffff, boundary.Allocations.Single().ClassId,
            "0xFFFF is an addressable counted class ID, not a TIE sentinel");

        var exhausted = Enumerable.Range(
            UyaCustomTieClassRegistry.FirstClassId,
            UyaCustomTieClassRegistry.LastClassId - UyaCustomTieClassRegistry.FirstClassId + 1)
            .ToHashSet();
        Throws<InvalidOperationException>(() => UyaCustomTieClassRegistry.AllocateImported(
            ProjectCustomClassRegistry.Empty, Id('e'), exhausted), "exhausted custom namespace");
        Throws<InvalidDataException>(() => UyaCustomTieClassRegistry.Validate(
            ProjectCustomClassRegistry.Empty with
            {
                Allocations = [new(new(Id('f'), AssetKind.Tie), 0x8fff,
                    new(ProjectCustomClassOrigin.ImportedModel))],
            }), "allocation below the UYA custom boundary");
    }

    private static void VerifyNativeBoundary()
    {
        using var iso = new MemoryStream(UyaProjectTests.CreateIso(), writable: false);
        var levelWad = UyaLooseLevelWadExtractor.ExtractPrimary(iso, 3).Bytes;
        var assets = UyaLevelPackService.IncludeSourceStaticAssets(
            levelWad, [], new HashSet<(TextureAssetFamily, int)>());
        var sourceTie = assets.Single(value => value.Family == TextureAssetFamily.Tie);
        var modelBytes = new byte[TieClassHeader.Size];
        BinaryPrimitives.WriteInt16LittleEndian(modelBytes.AsSpan(0x44), unchecked((short)0xffff));
        var boundaryTie = sourceTie with
        {
            AssetId = "custom-boundary",
            ClassId = 0xffff,
            ModelBytes = modelBytes,
        };
        var inputs = assets.Where(value => value != sourceTie).Append(boundaryTie).ToArray();
        var source = UyaLevelWadRenderPackageBuilder.ReadAssetSourceFiles(
            UyaLevelWadUnpacker.Unpack(levelWad).Files);
        var composition = StaticAssetComposer.Compose(
            GameId.UYA, source.HeaderBytes, source.AssetWadBytes, source.PaletteBytes, inputs);
        var header = LevelAssetReader.ReadHeader(composition.HeaderBytes);
        Equal(0xffff, LevelAssetReader.ReadModelDefinitions(
                composition.HeaderBytes, header.TieModelOffset, header.TieModelCount).Single().ModelId,
            "asset definition accepts the adapter maximum");
        Equal(0xffff, (int)(ushort)TieClassReader.Read(
                modelBytes, TieClassReadOptions.ForGameProfile(TieGameProfile.ForGame(GameId.UYA))).Header.OClass,
            "embedded TIE OClass retains the adapter maximum bits");

        var classList = UyaClassIdListWriter.Write([0xffff]);
        Equal(0xffff, BinaryPrimitives.ReadInt32LittleEndian(classList.AsSpan(sizeof(int))),
            "counted class list accepts 0xFFFF");
        var instance = UyaGameplayInstanceTemplates.CreateTie(0xffff, 1);
        Equal(0xffff, UyaTieInstancesReader.ReadInstance(instance).ClassId,
            "TIE instance accepts 0xFFFF");
    }

    private static async Task VerifyProjectRoundTripAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-custom-tie-registry-{Guid.NewGuid():N}");
        try
        {
            var workspace = await ForgeProjectWorkspace.CreateAsync(
                root,
                "Custom TIE registry",
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
                new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)),
                []);
            var first = await workspace.AttachAssetAsync(AssetKind.Tie, 1, "first custom tie"u8.ToArray());
            var second = await workspace.AttachAssetAsync(AssetKind.Tie, 1, "second custom tie"u8.ToArray());
            var registry = UyaCustomTieClassRegistry.AllocateImported(
                ProjectCustomClassRegistry.Empty, first.Id, new HashSet<int>());
            var state = workspace.CaptureState();
            workspace.RestoreState(state with { Content = state.Content with { CustomClasses = registry } });
            Equal(true, workspace.IsAssetReferenced(first.Id), "registry protects its attached asset");
            await workspace.SaveAsync();

            var reopened = await ForgeProjectWorkspace.OpenAsync(root);
            var allocation = reopened.Content.CustomClasses.Allocations.Single();
            Equal(first.Id, allocation.Asset.Id, "custom class Asset ID round trip");
            Equal(0x9000, allocation.ClassId, "custom class target ID round trip");
            Equal(ProjectCustomClassOrigin.ImportedModel, allocation.Provenance.Origin,
                "custom class provenance round trip");

            var duplicateClass = registry with
            {
                Allocations = registry.Allocations.Append(new(
                    new(second.Id, AssetKind.Tie),
                    registry.Allocations[0].ClassId,
                    new(ProjectCustomClassOrigin.ImportedModel))).ToArray(),
            };
            var reopenedState = reopened.CaptureState();
            Throws<InvalidDataException>(() => reopened.RestoreState(
                reopenedState with { Content = reopenedState.Content with { CustomClasses = duplicateClass } }),
                "duplicate target class validation");
            var collected = await reopened.CollectUnreferencedAssetsAsync();
            Equal(true, collected.SequenceEqual([second.Id]),
                "registry asset survives ordinary cleanup while an unreferenced asset is collected");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static int Target(UyaTieClassRebaseResult result, AssetId assetId) =>
        result.Registry.Allocations.Single(value => value.Asset.Id == assetId).ClassId;

    private static AssetId Id(char value) => AssetId.Parse(new string(value, AssetId.TextLength));

    private static void Throws<T>(Action action, string context) where T : Exception
    {
        try
        {
            action();
            throw new InvalidOperationException($"Expected {typeof(T).Name}: {context}");
        }
        catch (T)
        {
        }
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}

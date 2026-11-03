using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Skyboxes;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaAssetPlacementCompatibilityTests
{
    public static void Run()
    {
        var model = new AssetCatalogEntry(AssetId.Compute(AssetKind.Moby, 1, [1]), AssetKind.Moby, 1, 1,
            ["moby:0x2117"], ["vanilla"],
            [new("DL", "NTSC-U", "1.00", "level01", "assets/asset_wad.bin", 0, new string('a', 32))],
            DateTimeOffset.UnixEpoch, "test");
        Check(Model(model).CanPlace, "supported DL model can be placed in UYA");
        Check(Model(model with { Sources = [model.Sources[0] with { Game = "UYA" }] }).CanPlace,
            "native model can be placed");
        Check(!Model(model, blob: false).CanPlace, "missing model blob blocks placement");
        Check(!Model(model, classes: []).CanPlace, "missing class identity blocks placement");
        Check(!Model(model, region: "PAL").CanPlace && !Model(model, revision: "2.00").CanPlace,
            "conversion requires the supported target region and revision");
        foreach (var blocked in new[]
        {
            model with { CanonicalFormatVersion = 0 },
            model with { Tags = [] },
            model with { Tags = ["vanilla", "texture:placeholder"] },
            model with { Sources = [model.Sources[0] with { Revision = "2.00" }] },
            model with { Kind = AssetKind.Texture },
        }) Check(!Model(blocked).CanPlace, "ineligible model metadata blocks placement");
        foreach (var kind in new[] { AssetKind.Tie, AssetKind.Shrub, AssetKind.Moby })
            Check(Model(model with { Kind = kind }).CanPlace, "each supported model kind is convertible");

        var sky = model with { Kind = AssetKind.Sky, CanonicalFormatVersion = 0 };
        Check(Sky(sky).CanPlace, "supported DL shell can be placed in UYA");
        Check(Sky(sky with { Sources = [sky.Sources[0] with { Game = "UYA" }] }).CanPlace,
            "native shell can be placed");
        Check(!Sky(sky, blob: false).CanPlace && !Sky(sky, index: null).CanPlace,
            "shell placement requires blob and shell identity");
        Check(!Sky(sky, region: "PAL").CanPlace && !Sky(sky, revision: "2.00").CanPlace
            && !Sky(sky with { CanonicalFormatVersion = 1 }).CanPlace,
            "shell conversion requires supported target and source format");
        Check(Sky(sky, count: SkyboxFormat.MaxShellCount - 1).CanPlace
            && !Sky(sky, count: SkyboxFormat.MaxShellCount).CanPlace,
            "shell limit is enforced at the boundary");
    }

    private static (bool CanPlace, string? DisabledReason) Model(AssetCatalogEntry entry,
        string region = "NTSC-U", string revision = "1.00", bool blob = true, int[]? classes = null) =>
        UyaAssetPlacementService.PlacementCompatibility(entry, region, revision, classes ?? [0x2117], blob);

    private static (bool CanPlace, string? DisabledReason) Sky(AssetCatalogEntry entry,
        string region = "NTSC-U", string revision = "1.00", bool blob = true, int? index = 0, uint count = 0) =>
        UyaSkyShellEditorService.PlacementCompatibility(entry, region, revision, blob, index, count);

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}

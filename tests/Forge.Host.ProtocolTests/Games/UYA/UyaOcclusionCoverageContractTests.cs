using System.Buffers.Binary;
using RatchetPs2.Core.Games;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Games.UYA.Occlusion;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaOcclusionCoverageContractTests
{
    internal static void Run()
    {
        VerifySparseMasksAndSourceContract();
        VerifyAssetRelocation();
        VerifyEmptyAndBoundaryEncoding();
        VerifySparseTreePointerBudget();
    }

    private static void VerifySparseMasksAndSourceContract()
    {
        var source = SparseGrid();
        var before = source.ToArray();
        var grid = UyaOcclusionGridReader.Read(source);
        var coordinates = grid.Octants.Select(c => (c.X, c.Y, c.Z)).ToArray();
        Check(coordinates.SequenceEqual(new[] { (1, 2, 3), (3, 2, 3), (7, 4, 3), (9, 6, 5) }),
            "Missing X cells, Y rows, and Z slices must remain gaps.");
        var masks = DistinctMasks();
        var raw = OcclusionComposer.ReplaceMasks(GameId.UYA, source, masks);
        var decoded = UyaOcclusionGridReader.Read(raw);
        Check(raw.Length > source.Length, "Uncompacted masks can outgrow the source allocation.");
        Check(decoded.Octants.Select(c => (c.X, c.Y, c.Z)).SequenceEqual(coordinates),
            "Mask replacement must preserve the ordered source coordinates.");
        foreach (var cell in decoded.Octants)
            Check(raw.AsSpan(decoded.MasksOffset + cell.MaskIndex * 128, 128).SequenceEqual(masks[(cell.X, cell.Y, cell.Z)]),
                "Split source mask aliases must retain each cell's complete mask.");
        Check(raw.SequenceEqual(OcclusionComposer.ReplaceMasks(GameId.UYA, source,
            masks.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value))),
            "Input dictionary order must not change the native bytes.");

        var compacted = OcclusionComposer.RebuildGrid(GameId.UYA, source, masks);
        var compactGrid = UyaOcclusionGridReader.Read(compacted);
        Check(compacted.Length <= source.Length, "Source-mode compaction must fit its original allocation.");
        foreach (var cell in compactGrid.Octants)
        {
            var mask = compacted.AsSpan(compactGrid.MasksOffset + cell.MaskIndex * 128, 128);
            var expected = masks[(cell.X, cell.Y, cell.Z)];
            Check(mask[127] == expected[127], "Compaction must preserve each cell's reserved bit.");
            for (var i = 0; i < 128; i++)
                Check((mask[i] & expected[i]) == expected[i], "Compaction must never remove generated visibility.");
        }

        var moved = source.ToArray();
        U16(moved, 28, 2);
        Throws<InvalidDataException>(() => OcclusionComposer.ValidateGrid(GameId.UYA, source, moved, coordinates.Length));
        var missing = masks.Where(pair => pair.Key != (1, 2, 3)).ToDictionary(pair => pair.Key, pair => pair.Value);
        Throws<InvalidDataException>(() => OcclusionComposer.RebuildGrid(GameId.UYA, source, missing));
        var added = new Dictionary<(int, int, int), byte[]>(masks) { [(2, 2, 3)] = new byte[128] };
        Throws<InvalidDataException>(() => OcclusionComposer.ReplaceMasks(GameId.UYA, source, added));
        var replaced = new Dictionary<(int, int, int), byte[]>(missing) { [(2, 2, 3)] = new byte[128] };
        Throws<InvalidDataException>(() => OcclusionComposer.RebuildGrid(GameId.UYA, source, replaced));
        Throws<OperationCanceledException>(() => OcclusionComposer.ReplaceMasks(GameId.UYA, source, masks, new(true)));
        Check(source.SequenceEqual(before), "Replacement, rejection, and cancellation must leave source bytes unchanged.");
    }

    private static void VerifyAssetRelocation()
    {
        var source = SparseGrid();
        var larger = OcclusionComposer.ReplaceMasks(GameId.UYA, source, DistinctMasks());
        var header = new byte[0xc0];
        var adjacentOffset = 16 + source.Length;
        var assets = new byte[adjacentOffset + 32];
        assets.AsSpan(0, 16).Fill(0xa1);
        source.CopyTo(assets, 16);
        assets.AsSpan(adjacentOffset).Fill(0xd3);
        I32(header, 0x0c, 16);
        I32(header, 0x7c, assets.Length);
        I32(header, 0x8c, assets.Length);
        I32(header, 0xa4, adjacentOffset);
        var headerBefore = header.ToArray();
        var assetsBefore = assets.ToArray();

        var grown = LevelAssetComposer.ComposeAssetWad(GameId.UYA, header, assets, new(Occlusion: larger));
        var delta = larger.Length - source.Length;
        Check(grown.AssetWadBytes.Length == assets.Length + delta, "The generic asset composer permits occlusion growth.");
        Check(BinaryPrimitives.ReadInt32LittleEndian(grown.HeaderBytes.AsSpan(0xa4)) == adjacentOffset + delta,
            "Growth must relocate the adjacent asset pointer.");
        Check(BinaryPrimitives.ReadInt32LittleEndian(grown.HeaderBytes.AsSpan(0x7c)) == grown.AssetWadBytes.Length
            && BinaryPrimitives.ReadInt32LittleEndian(grown.HeaderBytes.AsSpan(0x8c)) == grown.AssetWadBytes.Length,
            "Growth must update scene-view and asset sizes.");
        Check(grown.AssetWadBytes.AsSpan(0, 16).SequenceEqual(assets.AsSpan(0, 16))
            && grown.AssetWadBytes.AsSpan(adjacentOffset + delta).SequenceEqual(assets.AsSpan(adjacentOffset)),
            "Growth must preserve the prefix and adjacent opaque payload.");
        Check(UyaOcclusionGridReader.ReadLevelAssetPayload(grown.HeaderBytes, grown.AssetWadBytes).SequenceEqual(larger),
            "The relocated occlusion payload must read back exactly.");

        var restored = LevelAssetComposer.ComposeAssetWad(GameId.UYA, grown.HeaderBytes, grown.AssetWadBytes, new(Occlusion: source));
        Check(restored.HeaderBytes.SequenceEqual(header) && restored.AssetWadBytes.SequenceEqual(assets),
            "Shrinking back must restore every original fixture byte.");
        Throws<OperationCanceledException>(() => LevelAssetComposer.ComposeAssetWad(
            GameId.UYA, header, assets, new(Occlusion: larger), new(true)));
        Check(header.SequenceEqual(headerBefore) && assets.SequenceEqual(assetsBefore),
            "Composition and cancellation must not mutate caller-owned source arrays.");
        VerifyPackedLevelRelocation(header, assets);
    }

    private static void VerifyPackedLevelRelocation(byte[] header, byte[] assets)
    {
        var source = LevelFixture(header, assets);
        var sourceBefore = source.ToArray();
        var expanded = ExpandedGrid();
        var composed = LevelAssetComposer.ComposeAssetWad(GameId.UYA, header, assets, new(Occlusion: expanded));
        var replacements = new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["assets/asset_header.bin"] = composed.HeaderBytes,
            ["assets/asset_wad_payload.bin"] = composed.AssetWadBytes,
        };
        var built = LevelArchiveBuilder.Build(GameId.UYA, source, replacements);
        Check(built.Succeeded && built.OutputBytes is not null, "Expanded coverage must pack into a complete level archive.");
        var output = built.OutputBytes!;
        Check(OcclusionComposer.ReadGrid(GameId.UYA, output).SequenceEqual(expanded),
            "Packed readback must contain exactly the replacement grid, including changed coordinates.");
        var decoded = UyaOcclusionGridReader.Read(OcclusionComposer.ReadGrid(GameId.UYA, output));
        Check(decoded.Octants.Count == 20 && decoded.Octants.All(c => c.X is >= 30 and < 50 && c.Y == 2 && c.Z == 3),
            "Packing must retain the added/removed coordinates rather than the original four cells.");
        Check(UyaLevelWadReader.ReadLevelWad(output).Data.Length > UyaLevelWadReader.ReadLevelWad(source).Data.Length,
            "Growth must cross a sector boundary and relocate later root payloads.");
        var beforeFiles = UyaLevelWadUnpacker.Unpack(source).Files.ToDictionary(file => file.Path, file => file.Bytes);
        var afterFiles = UyaLevelWadUnpacker.Unpack(output).Files.ToDictionary(file => file.Path, file => file.Bytes);
        foreach (var path in new[] { "assets/palette.bin", "level_wad/sound.bnk", "occlusion/occlusion.bin" })
            Check(beforeFiles[path].SequenceEqual(afterFiles[path]), "Packing must preserve unrelated palette, sound and auxiliary occlusion bytes.");
        Check(afterFiles["assets/asset_wad.bin"].AsSpan(composed.AssetWadBytes.Length - 32, 32)
            .SequenceEqual(assets.AsSpan(assets.Length - 32)), "Packing must preserve the adjacent opaque asset.");
        Check(output.SequenceEqual(LevelArchiveBuilder.Build(GameId.UYA, source, replacements).OutputBytes!),
            "Repeated complete-level composition must be deterministic.");
        Throws<InvalidDataException>(() => OcclusionComposer.ApplyToLevel(GameId.UYA, source, expanded, 20));
        Throws<OperationCanceledException>(() => LevelArchiveBuilder.Build(GameId.UYA, source, replacements, cancellationToken: new(true)));
        Check(source.SequenceEqual(sourceBefore), "Packing, source-mode rejection and cancellation must preserve the source archive.");
    }

    private static byte[] LevelFixture(byte[] header, byte[] assets)
    {
        const int sector = UyaLevelConstants.SectorSize;
        var bytes = new byte[4 * sector];
        I32(bytes, 0, UyaLevelConstants.LevelWadHeaderSize);
        I32(bytes, 0x10, 1); I32(bytes, 0x14, 1);
        I32(bytes, 0x18, 2); I32(bytes, 0x1c, 1);
        I32(bytes, 0x28, 3); I32(bytes, 0x2c, 1);
        I32(bytes, sector + 0x08, 0x80); I32(bytes, sector + 0x0c, header.Length);
        I32(bytes, sector + 0x10, 0x140); I32(bytes, sector + 0x14, 16);
        I32(bytes, sector + 0x48, 0x150); I32(bytes, sector + 0x4c, assets.Length);
        header.CopyTo(bytes, sector + 0x80);
        bytes.AsSpan(sector + 0x140, 16).Fill(0xa5);
        assets.CopyTo(bytes, sector + 0x150);
        bytes.AsSpan(2 * sector, sector).Fill(0x53);
        bytes.AsSpan(3 * sector, sector).Fill(0x6f);
        return bytes;
    }

    private static byte[] ExpandedGrid()
    {
        var bytes = new byte[64 + 20 * 128];
        I32(bytes, 0, 64); U16(bytes, 4, 3); U16(bytes, 6, 1); U16(bytes, 8, 3);
        U16(bytes, 12, 2); U16(bytes, 14, 1); U16(bytes, 16, 5);
        U16(bytes, 20, 30); U16(bytes, 22, 20);
        for (var i = 0; i < 20; i++)
        {
            U16(bytes, 24 + i * 2, i);
            bytes[64 + i * 128] = (byte)(i + 1);
            bytes[64 + i * 128 + 127] = (byte)(i % 2 == 0 ? 0 : 128);
        }
        return bytes;
    }

    private static void VerifyEmptyAndBoundaryEncoding()
    {
        var empty = new byte[8];
        I32(empty, 0, 8);
        Check(UyaOcclusionGridReader.Read(empty).Octants.Count == 0, "An empty Z table is structurally readable.");
        Throws<InvalidDataException>(() => OcclusionComposer.ReplaceMasks(GameId.UYA, empty, new Dictionary<(int, int, int), byte[]>()));
        Throws<InvalidDataException>(() => OcclusionComposer.ValidateGrid(GameId.UYA, SparseGrid(), empty, 0));

        var boundary = new byte[320];
        I32(boundary, 0, 32);
        U16(boundary, 4, ushort.MaxValue); U16(boundary, 6, 1); U16(boundary, 8, 3);
        U16(boundary, 12, ushort.MaxValue); U16(boundary, 14, 1); U16(boundary, 16, 5);
        U16(boundary, 20, ushort.MaxValue); U16(boundary, 22, 2); U16(boundary, 26, 1);
        var cells = UyaOcclusionGridReader.Read(boundary).Octants;
        Check(cells.Select(c => (c.X, c.Y, c.Z)).SequenceEqual(new[]
            { (65535, 65535, 65535), (65536, 65535, 65535) }),
            "Unsigned row starts do not imply that every decoded coordinate fits a ushort.");
        var masks = cells.ToDictionary(c => (c.X, c.Y, c.Z), _ => new byte[128]);
        var output = OcclusionComposer.ReplaceMasks(GameId.UYA, boundary, masks);
        Check(UyaOcclusionGridReader.Read(output).Octants.Select(c => (c.X, c.Y, c.Z))
            .SequenceEqual(cells.Select(c => (c.X, c.Y, c.Z))), "Boundary encoding must not silently wrap coordinates.");
    }

    private static void VerifySparseTreePointerBudget()
    {
        // Shared source rows fit, but serializing one row per slice exceeds the relative-pointer budget.
        const int slices = 20_000;
        const int yAt = 8 + slices * 2;
        const int xAt = yAt + 8;
        const int masksAt = (xAt + 6 + 15) & ~15;
        var source = new byte[(masksAt + 128 + 63) & ~63];
        I32(source, 0, masksAt); U16(source, 6, slices);
        for (var i = 0; i < slices; i++) U16(source, 8 + i * 2, yAt / 4);
        U16(source, yAt + 2, 1); U16(source, yAt + 4, xAt / 4); U16(source, xAt + 2, 1);
        var cells = UyaOcclusionGridReader.Read(source).Octants;
        Check(cells.Count == slices, "The aliased source fixture must be structurally readable.");
        var sharedMask = new byte[128];
        var masks = cells.ToDictionary(c => (c.X, c.Y, c.Z), _ => sharedMask);
        var exception = Throws<InvalidDataException>(() => OcclusionComposer.ReplaceMasks(GameId.UYA, source, masks));
        Check(exception.Message.Contains("pointer capacity", StringComparison.Ordinal),
            "A cell count below the authoring limit can still exceed the native tree-pointer budget.");
    }

    private static byte[] SparseGrid()
    {
        var bytes = new byte[320];
        I32(bytes, 0, 64);
        U16(bytes, 4, 3); U16(bytes, 6, 3); U16(bytes, 8, 4); U16(bytes, 12, 12);
        U16(bytes, 16, 2); U16(bytes, 18, 3); U16(bytes, 20, 7); U16(bytes, 24, 10);
        U16(bytes, 28, 1); U16(bytes, 30, 3); U16(bytes, 34, ushort.MaxValue); U16(bytes, 36, 1);
        U16(bytes, 40, 7); U16(bytes, 42, 1);
        U16(bytes, 48, 6); U16(bytes, 50, 1); U16(bytes, 52, 14);
        U16(bytes, 56, 9); U16(bytes, 58, 1); U16(bytes, 60, 1);
        bytes[64] = 1; bytes[64 + 128 + 127] = 128;
        return bytes;
    }

    private static Dictionary<(int, int, int), byte[]> DistinctMasks()
    {
        var coordinates = new[] { (1, 2, 3), (3, 2, 3), (7, 4, 3), (9, 6, 5) };
        return coordinates.Select((coordinate, i) =>
        {
            var mask = new byte[128];
            mask[0] = (byte)(1 << i);
            mask[127] = (byte)(i % 2 == 1 ? 128 : 0);
            return (coordinate, mask);
        }).ToDictionary(pair => pair.coordinate, pair => pair.mask);
    }

    private static void U16(byte[] bytes, int at, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), checked((ushort)value));

    private static void I32(byte[] bytes, int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), value);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T exception) { return exception; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}

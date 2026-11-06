using System.Buffers.Binary;
using RatchetPs2.Games.UYA.Level;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaTieInstanceCapacityTests
{
    public static void Run()
    {
        foreach (var count in new[] { 0, 4095, 4096, 4604 })
        {
            var instances = new byte[0x10 + count * 0x60];
            BinaryPrimitives.WriteInt32LittleEndian(instances, count);
            var level = new UyaLevelWad(0, 0, 0, 0, default, default, default, default, [], [], []);
            var package = new UyaLevelWadPackage(level,
                [new("gameplay/core/tie_instances.bin", instances, "application/octet-stream")]);
            var header = new byte[0x90];
            var inventory = new UyaLevelWadInventory(level,
            [
                new("level_wad/level_data.wad", null, 0, 0, UyaContainerCompression.None, "", ReadOnlyMemory<byte>.Empty,
                [
                    new("assets/asset_header.bin", [], 0, header.Length, 0, header.Length, 1,
                        UyaContainerCompression.None, "", header),
                    new("assets/asset_wad.bin", [], 0, 0, 0, 0, 1,
                        UyaContainerCompression.None, "", ReadOnlyMemory<byte>.Empty)
                ], []),
                new("assets/asset_wad.bin", null, 0, 0, UyaContainerCompression.None, "", ReadOnlyMemory<byte>.Empty, [], [])
            ]);
            if (count <= 4095)
                UyaLevelWadValidator.Validate(inventory, package);
            else
            {
                try
                {
                    UyaLevelWadValidator.Validate(inventory, package);
                    throw new Exception("Expected the native Tie instance limit to block packing.");
                }
                catch (InvalidDataException error)
                {
                    if (!error.Message.Contains("4095", StringComparison.Ordinal)
                        || !error.Message.Contains(count.ToString(), StringComparison.Ordinal))
                        throw new Exception("The Tie capacity diagnostic must identify the native limit and actual count.");
                }
            }
        }
        Console.WriteLine("Native Tie capacity: 4095 instances accepted; larger maps rejected with their actual count.");
    }
}

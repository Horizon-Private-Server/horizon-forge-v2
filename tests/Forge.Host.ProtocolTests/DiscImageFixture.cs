using System.Buffers.Binary;
using System.Text;

internal static class DiscImageFixture
{
    internal static void AddIdentity(byte[] bytes, string serial = "SCUS_973.53", string videoMode = "NTSC")
    {
        const int rootSector = 20;
        const int systemSector = 21;

        var descriptor = bytes.AsSpan(16 * 2048, 2048);
        descriptor[0] = 1;
        "CD001"u8.CopyTo(descriptor[1..]);
        descriptor[6] = 1;
        WriteDirectoryRecord(descriptor[156..], rootSector, 2048, [0]);

        var root = bytes.AsSpan(rootSector * 2048, 2048);
        WriteDirectoryRecord(root, systemSector, 128, Encoding.ASCII.GetBytes("SYSTEM.CNF;1"));
        var configuration = Encoding.ASCII.GetBytes($"BOOT2 = cdrom0:\\{serial};1\r\nVER = 1.00\r\nVMODE = {videoMode}\r\n");
        configuration.CopyTo(bytes.AsSpan(systemSector * 2048));
    }

    private static void WriteDirectoryRecord(Span<byte> destination, int sector, int length, ReadOnlySpan<byte> name)
    {
        var recordLength = 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
        destination[0] = checked((byte)recordLength);
        BinaryPrimitives.WriteInt32LittleEndian(destination[2..], sector);
        BinaryPrimitives.WriteInt32BigEndian(destination[6..], sector);
        BinaryPrimitives.WriteInt32LittleEndian(destination[10..], length);
        BinaryPrimitives.WriteInt32BigEndian(destination[14..], length);
        destination[25] = 2;
        destination[28] = 1;
        destination[31] = 1;
        destination[32] = checked((byte)name.Length);
        name.CopyTo(destination[33..]);
    }

}

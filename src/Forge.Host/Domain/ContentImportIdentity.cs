using System.Security.Cryptography;
using System.Text;

namespace Forge.Host.Domain;

internal static class ContentImportIdentity
{
    public static Guid EntityGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new(bytes);
    }

    // Sky and light imports historically used a different byte order and version nibble.
    public static Guid SkyOrLightGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new(bytes, bigEndian: true);
    }
}

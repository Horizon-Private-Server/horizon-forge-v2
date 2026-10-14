using System.Security.Cryptography;
using System.Text.Json;
using Forge.Updater;

return await RunAsync(args);

static async Task<int> RunAsync(string[] arguments)
{
    try
    {
        if (arguments.Length != 4
            || !Guid.TryParse(arguments[1], out _)
            || arguments[2].Length != 64
            || !int.TryParse(arguments[3], out var sourceProcessId)
            || sourceProcessId <= 0)
            throw new ArgumentException("Usage: Forge.Updater <handoff> <nonce> <sha256> <source-pid>");
        var handoffPath = Path.GetFullPath(arguments[0]);
        var expectedPath = Path.Combine(AppContext.BaseDirectory, "handoff.json");
        if (!handoffPath.Equals(expectedPath, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Update handoff must be beside the updater helper.");
        var bytes = await File.ReadAllBytesAsync(handoffPath);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(digest), Convert.FromHexString(arguments[2])))
            throw new InvalidDataException("Update handoff fingerprint does not match.");
        var handoff = JsonSerializer.Deserialize(bytes, UpdaterJsonContext.Default.ActivationHandoff)
            ?? throw new InvalidDataException("Update handoff is invalid.");
        if (handoff.Nonce != arguments[1]) throw new InvalidDataException("Update handoff nonce does not match.");
        await new ActivationRunner(handoff, AppContext.BaseDirectory).RunAsync(sourceProcessId);
        return 0;
    }
    catch (Exception exception)
    {
        try
        {
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "failure.txt"), exception.ToString());
        }
        catch { }
        return 1;
    }
}

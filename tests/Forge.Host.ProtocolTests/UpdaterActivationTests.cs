using System.Security.Cryptography;
using System.Text.Json;
using Forge.Updater;

internal static class UpdaterActivationTests
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        await VerifySuccessfulActivationAsync();
        await VerifySymlinkAliasActivationAsync();
        await VerifyInterruptedRenameRecoveryAsync();
        await VerifyFailedStartupRollsBackAsync();
    }

    private static async Task VerifySuccessfulActivationAsync()
    {
        var fixture = await CreateFixtureAsync("#!/bin/sh\nhealth=\"\"\nnonce=\"\"\nwhile [ \"$#\" -gt 0 ]; do\n"
            + "  [ \"$1\" = \"--forge-update-health\" ] && health=$2 && shift 2 && continue\n"
            + "  [ \"$1\" = \"--forge-update-nonce\" ] && nonce=$2 && shift 2 && continue\n  shift\ndone\n"
            + "[ -n \"$health\" ] && printf '{\"schemaVersion\":1,\"nonce\":\"%s\",\"version\":\"2.1.0\"}\\n' \"$nonce\" > \"$health\"\n",
            "success");
        try
        {
            await fixture.Runner.RunAsync(int.MaxValue);
            Equal("2.1.0", ReadVersion(fixture.TargetRoot), "activated version");
            Equal("2.0.0", ReadVersion(fixture.RollbackRoot), "retained rollback version");
            Equal("healthy", ReadCheckpoint(fixture.ActivationRoot), "healthy activation checkpoint");
        }
        finally
        {
            await Task.Delay(250);
            Directory.Delete(fixture.Root, true);
        }
    }

    private static async Task VerifyFailedStartupRollsBackAsync()
    {
        var fixture = await CreateFixtureAsync("#!/bin/sh\nexit 1\n", "rollback");
        try
        {
            await fixture.Runner.RunAsync(int.MaxValue);
            Equal("2.0.0", ReadVersion(fixture.TargetRoot), "restored version");
            Equal("rolled-back", ReadCheckpoint(fixture.ActivationRoot), "rollback checkpoint");
            if (Directory.Exists(fixture.RollbackRoot))
                throw new InvalidOperationException("Consumed rollback directory still exists.");
        }
        finally
        {
            await Task.Delay(250);
            Directory.Delete(fixture.Root, true);
        }
    }

    private static async Task VerifySymlinkAliasActivationAsync()
    {
        var fixture = await CreateFixtureAsync("#!/bin/sh\nhealth=\"$2\"\nnonce=\"$4\"\n"
            + "printf '{\"schemaVersion\":1,\"nonce\":\"%s\",\"version\":\"2.1.0\"}\\n' \"$nonce\" > \"$health\"\n",
            "symlink-alias", useSymlinkAlias: true);
        try
        {
            if (!PhysicalPath.Equals(fixture.AliasRoot!, fixture.Root))
                throw new InvalidOperationException("Physical path comparison did not resolve the directory alias.");
            await fixture.Runner.RunAsync(int.MaxValue);
            Equal("2.1.0", ReadVersion(fixture.TargetRoot), "symlink-alias activated version");
            Equal("healthy", ReadCheckpoint(fixture.ActivationRoot), "symlink-alias activation checkpoint");
        }
        finally
        {
            await Task.Delay(250);
            Directory.Delete(fixture.AliasRoot!, false);
            Directory.Delete(fixture.Root, true);
        }
    }

    private static async Task VerifyInterruptedRenameRecoveryAsync()
    {
        var fixture = await CreateFixtureAsync("#!/bin/sh\nhealth=\"$2\"\nnonce=\"$4\"\n"
            + "printf '{\"schemaVersion\":1,\"nonce\":\"%s\",\"version\":\"2.1.0\"}\\n' \"$nonce\" > \"$health\"\n",
            "rename-recovery");
        try
        {
            Directory.Move(fixture.TargetRoot, fixture.RollbackRoot);
            await fixture.Runner.RunAsync(int.MaxValue);
            Equal("2.1.0", ReadVersion(fixture.TargetRoot), "recovered activated version");
            Equal("2.0.0", ReadVersion(fixture.RollbackRoot), "recovered rollback version");
            Equal("healthy", ReadCheckpoint(fixture.ActivationRoot), "recovered activation checkpoint");
        }
        finally
        {
            await Task.Delay(250);
            Directory.Delete(fixture.Root, true);
        }
    }

    private static async Task<Fixture> CreateFixtureAsync(string newExecutable, string name, bool useSymlinkAlias = false)
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-updater-{name}-{Guid.NewGuid():N}");
        var nonce = Guid.NewGuid().ToString();
        var target = Path.Combine(root, "forge");
        var stage = Path.Combine(root, "updates", "staging", Guid.NewGuid().ToString(), "horizon-forge-linux-x64");
        var activation = Path.Combine(root, "updates", "activation", nonce);
        Directory.CreateDirectory(root);
        var oldHash = await WritePackageAsync(target, "2.0.0", "#!/bin/sh\nexit 0\n");
        var newHash = await WritePackageAsync(stage, "2.1.0", newExecutable);
        Directory.CreateDirectory(activation);
        string? aliasRoot = null;
        if (useSymlinkAlias)
        {
            aliasRoot = $"{root}-alias";
            Directory.CreateSymbolicLink(aliasRoot, root);
            stage = Path.Combine(aliasRoot, Path.GetRelativePath(root, stage));
        }
        var handoff = new ActivationHandoff(1, nonce, target, stage, oldHash, newHash, "2.1.0",
            "horizon-forge", "linux", 10);
        var rollback = Path.Combine(root, ".forge.forge-rollback");
        return new Fixture(root, target, rollback, activation, aliasRoot, new ActivationRunner(handoff, activation));
    }

    private static async Task<string> WritePackageAsync(string root, string version, string executableContents)
    {
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "horizon-forge");
        await File.WriteAllTextAsync(executable, executableContents);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var bytes = await File.ReadAllBytesAsync(executable);
        var entry = new PackageFile("horizon-forge", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var manifest = new PackageManifest(1, "horizon-forge", version, "linux", "horizon-forge-linux-x64",
            "horizon-forge", 1, bytes.Length, ["horizon-forge"], [entry]);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, UpdaterJsonContext.Default.PackageManifest);
        await File.WriteAllBytesAsync(Path.Combine(root, "forge-package.json"), manifestBytes);
        return Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
    }

    private static string ReadVersion(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "forge-package.json")));
        return document.RootElement.GetProperty("version").GetString()!;
    }

    private static string ReadCheckpoint(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "state.json")));
        return document.RootElement.GetProperty("checkpoint").GetString()!;
    }

    private static void Equal(string expected, string actual, string context)
    {
        if (expected != actual) throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    private sealed record Fixture(
        string Root,
        string TargetRoot,
        string RollbackRoot,
        string ActivationRoot,
        string? AliasRoot,
        ActivationRunner Runner);
}

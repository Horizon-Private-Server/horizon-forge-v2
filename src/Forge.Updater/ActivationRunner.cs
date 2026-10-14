using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Forge.Updater;

internal sealed class ActivationRunner
{
    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private readonly ActivationHandoff handoff;
    private readonly string directory;
    private readonly string statePath;
    private readonly string healthPath;
    private readonly string candidateRoot;
    private readonly string rollbackRoot;

    public ActivationRunner(ActivationHandoff handoff, string directory)
    {
        this.handoff = handoff;
        this.directory = directory;
        statePath = Path.Combine(directory, "state.json");
        healthPath = Path.Combine(directory, "health.json");
        var parent = Path.GetDirectoryName(handoff.TargetRoot)!;
        var name = Path.GetFileName(handoff.TargetRoot);
        candidateRoot = Path.Combine(parent, $".{name}.forge-candidate-{handoff.Nonce}");
        rollbackRoot = Path.Combine(parent, $".{name}.forge-rollback");
    }

    public async Task RunAsync(int sourceProcessId)
    {
        ValidateHandoff();
        await WriteStateAsync("waiting-for-exit", null);
        await WaitForExitAsync(sourceProcessId, TimeSpan.FromMinutes(5));
        var targetFingerprint = Directory.Exists(handoff.TargetRoot)
            ? PackageVerifier.ManifestFingerprint(handoff.TargetRoot) : null;

        if (FixedEquals(targetFingerprint, handoff.ExpectedPackageManifestSha256) && Directory.Exists(rollbackRoot))
        {
            await PackageVerifier.VerifyAsync(handoff.TargetRoot, handoff.ExpectedPackageManifestSha256,
                handoff.ExpectedVersion);
            await PackageVerifier.VerifyAsync(rollbackRoot, handoff.ExpectedTargetPackageManifestSha256);
        }
        else
        {
            await PrepareAndActivateAsync(targetFingerprint);
        }

        if (await IsHealthyAsync())
        {
            await WriteStateAsync("healthy", null);
            return;
        }
        await WriteStateAsync("activated", null);
        var process = StartPackage(handoff.TargetRoot, [
            "--forge-update-health", healthPath,
            "--forge-update-nonce", handoff.Nonce,
        ]);
        await WriteStateAsync("launched", null);
        if (await WaitForHealthAsync(process, TimeSpan.FromSeconds(handoff.HealthTimeoutSeconds)))
        {
            process.Dispose();
            await WriteStateAsync("healthy", null);
            return;
        }
        await StopAsync(process);
        await RollBackAsync();
    }

    private async Task PrepareAndActivateAsync(string? targetFingerprint)
    {
        if (targetFingerprint is null && Directory.Exists(rollbackRoot))
        {
            await PackageVerifier.VerifyAsync(rollbackRoot, handoff.ExpectedTargetPackageManifestSha256);
        }
        else
        {
            if (!FixedEquals(targetFingerprint, handoff.ExpectedTargetPackageManifestSha256))
                throw new InvalidDataException("Active installation changed after staging.");
            await PackageVerifier.VerifyAsync(handoff.TargetRoot, handoff.ExpectedTargetPackageManifestSha256);
        }
        await PackageVerifier.VerifyAsync(handoff.StageRoot, handoff.ExpectedPackageManifestSha256,
            handoff.ExpectedVersion);

        if (Directory.Exists(candidateRoot))
        {
            PackageVerifier.AssertSafeRemovalRoot(candidateRoot);
            Directory.Delete(candidateRoot, true);
        }
        await WriteStateAsync("copying", null);
        await PackageVerifier.CopyAsync(handoff.StageRoot, candidateRoot);
        await PackageVerifier.VerifyAsync(candidateRoot, handoff.ExpectedPackageManifestSha256,
            handoff.ExpectedVersion);
        await WriteStateAsync("candidate-ready", null);

        if (Directory.Exists(handoff.TargetRoot))
        {
            if (Directory.Exists(rollbackRoot))
            {
                PackageVerifier.AssertSafeRemovalRoot(rollbackRoot);
                await PackageVerifier.VerifyAsync(rollbackRoot, PackageVerifier.ManifestFingerprint(rollbackRoot));
                Directory.Delete(rollbackRoot, true);
            }
            Directory.Move(handoff.TargetRoot, rollbackRoot);
            await WriteStateAsync("old-renamed", null);
        }
        try
        {
            Directory.Move(candidateRoot, handoff.TargetRoot);
        }
        catch
        {
            if (!Directory.Exists(handoff.TargetRoot) && Directory.Exists(rollbackRoot))
                Directory.Move(rollbackRoot, handoff.TargetRoot);
            throw;
        }
        await PackageVerifier.VerifyAsync(handoff.TargetRoot, handoff.ExpectedPackageManifestSha256,
            handoff.ExpectedVersion);
    }

    private async Task RollBackAsync()
    {
        await WriteStateAsync("rolling-back", "Updated Forge did not report a healthy startup.");
        try
        {
            if (!Directory.Exists(rollbackRoot)) throw new DirectoryNotFoundException("Rollback installation is missing.");
            await PackageVerifier.VerifyAsync(rollbackRoot, handoff.ExpectedTargetPackageManifestSha256);
            await PackageVerifier.VerifyAsync(handoff.TargetRoot, handoff.ExpectedPackageManifestSha256,
                handoff.ExpectedVersion);
            if (Directory.Exists(candidateRoot))
            {
                PackageVerifier.AssertSafeRemovalRoot(candidateRoot);
                Directory.Delete(candidateRoot, true);
            }
            if (Directory.Exists(handoff.TargetRoot)) Directory.Move(handoff.TargetRoot, candidateRoot);
            Directory.Move(rollbackRoot, handoff.TargetRoot);
            if (Directory.Exists(candidateRoot)) Directory.Delete(candidateRoot, true);
            await PackageVerifier.VerifyAsync(handoff.TargetRoot, handoff.ExpectedTargetPackageManifestSha256);
            StartPackage(handoff.TargetRoot, []).Dispose();
            await WriteStateAsync("rolled-back", "The previous Forge version was restored and launched.");
        }
        catch (Exception exception)
        {
            await WriteStateAsync("recovery-required", exception.Message);
            throw;
        }
    }

    private void ValidateHandoff()
    {
        var activationDirectory = new DirectoryInfo(directory.TrimEnd(Path.DirectorySeparatorChar));
        var activationRoot = activationDirectory.Parent;
        var updatesRoot = activationRoot?.Parent;
        var stagingRoot = updatesRoot is null ? null : Path.Combine(updatesRoot.FullName, "staging");
        if (handoff.SchemaVersion != 1 || !Guid.TryParse(handoff.Nonce, out _)
            || handoff.HealthTimeoutSeconds is < 10 or > 600
            || handoff.Platform != (OperatingSystem.IsWindows() ? "windows" : "linux")
            || !Sha256Pattern.IsMatch(handoff.ExpectedTargetPackageManifestSha256)
            || !Sha256Pattern.IsMatch(handoff.ExpectedPackageManifestSha256)
            || !Path.IsPathFullyQualified(handoff.TargetRoot) || !Path.IsPathFullyQualified(handoff.StageRoot)
            || !Path.GetFullPath(handoff.TargetRoot).Equals(handoff.TargetRoot, PathComparison)
            || !Path.GetFullPath(handoff.StageRoot).Equals(handoff.StageRoot, PathComparison)
            || handoff.TargetRoot.Equals(handoff.StageRoot, PathComparison)
            || handoff.TargetRoot.Equals(Path.GetPathRoot(handoff.TargetRoot), PathComparison)
            || activationDirectory.Name != handoff.Nonce || activationRoot?.Name != "activation"
            || stagingRoot is null || !IsWithin(stagingRoot, handoff.StageRoot)
            || (updatesRoot is not null && IsWithin(updatesRoot.FullName, handoff.TargetRoot))
            || Path.GetFileName(handoff.Executable) != handoff.Executable)
            throw new InvalidDataException("Update activation handoff is invalid.");
    }

    private static bool IsWithin(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(candidate));
        return relative == "." || (!relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison)
            && relative != ".." && !Path.IsPathFullyQualified(relative));
    }

    private Process StartPackage(string root, IReadOnlyList<string> arguments)
    {
        var executable = Path.Combine(root, handoff.Executable);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = root };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Forge could not be restarted.");
    }

    private async Task<bool> WaitForHealthAsync(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsHealthyAsync()) return true;
            if (process.HasExited) return false;
            await Task.Delay(250);
        }
        return await IsHealthyAsync();
    }

    private async Task<bool> IsHealthyAsync()
    {
        try
        {
            var info = new FileInfo(healthPath);
            if (!info.Exists || info.Length is <= 0 or > 4096) return false;
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(healthPath));
            var root = document.RootElement;
            return root.GetProperty("schemaVersion").GetInt32() == 1
                && root.GetProperty("nonce").GetString() == handoff.Nonce
                && root.GetProperty("version").GetString() == handoff.ExpectedVersion;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task WriteStateAsync(string checkpoint, string? message)
    {
        var value = new ActivationState(1, handoff.Nonce, checkpoint, message, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var temporary = Path.Combine(directory, $".state.{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(value, UpdaterJsonContext.Default.ActivationState));
        File.Move(temporary, statePath, true);
    }

    private static async Task WaitForExitAsync(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var cancellation = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (ArgumentException) { }
    }

    private static async Task StopAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(true);
            await process.WaitForExitAsync();
        }
        process.Dispose();
    }

    private static bool FixedEquals(string? left, string right) => left is not null
        && left.Length == right.Length
        && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

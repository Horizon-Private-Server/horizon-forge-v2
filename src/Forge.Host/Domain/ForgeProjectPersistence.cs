using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Host.Domain;

internal static class ForgeProjectPersistence
{
    public const string RecoveryDirectoryName = "recovery";
    public const int MaxRecoverySnapshots = 10;
    public const long MaxRecoveryBytes = 256L * 1024 * 1024;
    private const long MaxManifestBytes = 1024 * 1024;
    private const long MaxContentBytes = 512L * 1024 * 1024;
    private const string SaveJournalDirectoryName = ".save-journal";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        MaxDepth = 64,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<LoadedProject> LoadAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(rootPath);
        await RecoverInterruptedSaveAsync(root, cancellationToken);
        return await LoadDirectoryAsync(root, cancellationToken);
    }

    public static async Task<ForgeProjectManifest> LoadManifestAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(rootPath);
        var manifestPath = Path.Combine(root, ForgeProjectWorkspace.ManifestFileName);
        if (new FileInfo(manifestPath).Length > MaxManifestBytes)
            throw new InvalidDataException("Project manifest exceeds the size limit.");
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        var version = ReadVersion(manifestBytes);
        if (version != ProjectSchema.CurrentVersion) throw new UnsupportedProjectSchemaException(version);
        var manifest = Deserialize<ForgeProjectManifest>(manifestBytes, "Project manifest");
        ForgeProjectValidation.ValidateManifest(root, manifest);
        return manifest;
    }

    public static async Task SaveAsync(
        string rootPath,
        ForgeProjectManifest manifest,
        ForgeProjectContent content,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(rootPath);
        await RecoverInterruptedSaveAsync(root, cancellationToken);
        var manifestPath = Path.Combine(root, ForgeProjectWorkspace.ManifestFileName);
        var contentPath = ResolveRelativePath(root, manifest.Content);
        var manifestExists = File.Exists(manifestPath);
        var previousRelativeContentPath = manifest.Content;
        var previousContentPath = contentPath;
        if (manifestExists)
        {
            var previousManifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
            using var previousManifest = ProjectSchema.Parse(previousManifestBytes);
            previousRelativeContentPath = previousManifest.RootElement.GetProperty("content").GetString()
                ?? throw new InvalidDataException("Project manifest has no content path.");
            previousContentPath = ResolveRelativePath(root, previousRelativeContentPath);
            if (!File.Exists(previousContentPath))
                throw new InvalidDataException("Project save is incomplete and has no recovery journal.");
        }
        else if (File.Exists(contentPath))
        {
            throw new InvalidDataException("Project save is incomplete and has no recovery journal.");
        }

        if (manifestExists)
            await CreateSaveJournalAsync(
                root, manifestPath, previousContentPath, previousRelativeContentPath, cancellationToken);
        try
        {
            await WriteFileSafelyAsync(contentPath, Compress(Serialize(content)), cancellationToken);
            await WriteFileSafelyAsync(manifestPath, Serialize(manifest), cancellationToken);
            if (!SamePath(previousContentPath, contentPath)) File.Delete(previousContentPath);
            DeleteDirectory(Path.Combine(root, RecoveryDirectoryName, SaveJournalDirectoryName));
        }
        catch
        {
            if (manifestExists)
            {
                await RecoverInterruptedSaveAsync(root, CancellationToken.None);
                if (!SamePath(previousContentPath, contentPath)) File.Delete(contentPath);
            }
            else
            {
                File.Delete(manifestPath);
                File.Delete(contentPath);
            }
            throw;
        }
    }

    public static async Task<ProjectRecoverySnapshot?> WriteRecoveryAsync(
        string rootPath,
        ForgeProjectManifest manifest,
        ForgeProjectContent content,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(rootPath);
        var manifestBytes = Serialize(manifest);
        var contentBytes = Serialize(content);
        var storedContentBytes = Compress(contentBytes);
        var fingerprint = Fingerprint(manifestBytes, contentBytes);
        var existing = await ListRecoveriesAsync(root, cancellationToken);
        if (existing.FirstOrDefault()?.Fingerprint == fingerprint) return existing[0];
        if (manifestBytes.LongLength + storedContentBytes.LongLength > MaxRecoveryBytes) return null;

        var created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var id = $"{created}-{Guid.NewGuid():N}";
        var recoveryRoot = Path.Combine(root, RecoveryDirectoryName);
        var temporary = Path.Combine(recoveryRoot, $".snapshot-{Guid.NewGuid():N}");
        var destination = Path.Combine(recoveryRoot, id);
        Directory.CreateDirectory(temporary);
        try
        {
            await WriteFileSafelyAsync(Path.Combine(temporary, ForgeProjectWorkspace.ManifestFileName), manifestBytes, cancellationToken);
            await WriteFileSafelyAsync(
                ResolveRelativePath(temporary, manifest.Content), storedContentBytes, cancellationToken);
            Directory.Move(temporary, destination);
        }
        finally
        {
            DeleteDirectory(temporary);
        }

        var snapshots = await TrimRecoveriesAsync(root, cancellationToken);
        return snapshots.SingleOrDefault(snapshot => snapshot.Id == id);
    }

    public static async Task<IReadOnlyList<ProjectRecoverySnapshot>> ListRecoveriesAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var recoveryRoot = Path.Combine(Path.GetFullPath(rootPath), RecoveryDirectoryName);
        if (!Directory.Exists(recoveryRoot)) return [];
        var snapshots = new List<ProjectRecoverySnapshot>();
        foreach (var directory in Directory.EnumerateDirectories(recoveryRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(directory);
            if (!TryParseRecoveryId(id, out var created)) continue;
            var loaded = await LoadDirectoryAsync(directory, cancellationToken);
            var manifestBytes = Serialize(loaded.Manifest);
            var contentBytes = Serialize(loaded.Content);
            snapshots.Add(new(
                id,
                created,
                loaded.Manifest.Name,
                loaded.Content.Entities.Count,
                Fingerprint(manifestBytes, contentBytes),
                DirectorySize(directory)));
        }
        return snapshots.OrderByDescending(snapshot => snapshot.CreatedUnixMilliseconds).ThenByDescending(snapshot => snapshot.Id).ToArray();
    }

    public static async Task<LoadedProject> LoadRecoveryAsync(
        string rootPath,
        string recoveryId,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseRecoveryId(recoveryId, out _)) throw new ArgumentException("Recovery ID is invalid.", nameof(recoveryId));
        var directory = Path.Combine(Path.GetFullPath(rootPath), RecoveryDirectoryName, recoveryId);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"Project recovery {recoveryId} does not exist.");
        return await LoadDirectoryAsync(directory, cancellationToken);
    }

    public static byte[] Serialize<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions).Append((byte)'\n').ToArray();

    public static string Fingerprint(ForgeProjectManifest manifest, ForgeProjectContent content) =>
        Fingerprint(Serialize(manifest), Serialize(content));

    public static string ResolveRelativePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\\'))
            throw new InvalidDataException("Project content path must be a portable relative path.");
        var segments = relativePath.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("Project content path contains an invalid segment.");
        var resolved = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        if (!resolved.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Project content path escapes the project root.");
        return resolved;
    }

    private static async Task<LoadedProject> LoadDirectoryAsync(string root, CancellationToken cancellationToken)
    {
        var manifest = await LoadManifestAsync(root, cancellationToken);
        var storedContentBytes = await ReadContentFileAsync(
            ResolveRelativePath(root, manifest.Content), cancellationToken);
        var contentBytes = Decompress(storedContentBytes);
        var contentVersion = ReadVersion(contentBytes);
        if (contentVersion != ProjectSchema.CurrentVersion)
            throw new UnsupportedProjectSchemaException(contentVersion);
        var content = Deserialize<ForgeProjectContent>(contentBytes, "Project content");
        return new(manifest, content);
    }

    private static int ReadVersion(byte[] bytes)
    {
        using var document = ProjectSchema.Parse(bytes);
        return document.RootElement.GetProperty("schemaVersion").GetInt32();
    }

    internal static T Deserialize<T>(byte[] bytes, string description) =>
        JsonSerializer.Deserialize<T>(bytes, JsonOptions)
        ?? throw new InvalidDataException($"{description} is empty.");

    private static async Task<byte[]> ReadContentFileAsync(string path, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > MaxContentBytes)
            throw new InvalidDataException("Project content exceeds the size limit.");
        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(bytes);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] bytes)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = gzip.Read(buffer)) > 0)
        {
            if (output.Length + read > MaxContentBytes)
                throw new InvalidDataException("Decompressed project content exceeds the size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static async Task CreateSaveJournalAsync(
        string root,
        string manifestPath,
        string contentPath,
        string relativeContentPath,
        CancellationToken cancellationToken)
    {
        var recoveryRoot = Path.Combine(root, RecoveryDirectoryName);
        var journal = Path.Combine(recoveryRoot, SaveJournalDirectoryName);
        var temporary = Path.Combine(recoveryRoot, $".save-journal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            await CopyFileAsync(manifestPath, Path.Combine(temporary, ForgeProjectWorkspace.ManifestFileName), cancellationToken);
            await CopyFileAsync(contentPath, ResolveRelativePath(temporary, relativeContentPath), cancellationToken);
            Directory.Move(temporary, journal);
        }
        finally
        {
            DeleteDirectory(temporary);
        }
    }

    private static async Task RecoverInterruptedSaveAsync(string root, CancellationToken cancellationToken)
    {
        var recoveryRoot = Path.Combine(root, RecoveryDirectoryName);
        var journal = Path.Combine(recoveryRoot, SaveJournalDirectoryName);
        if (Directory.Exists(journal))
        {
            var manifestPath = Path.Combine(journal, ForgeProjectWorkspace.ManifestFileName);
            var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
            using var document = ProjectSchema.Parse(manifestBytes);
            var relativeContentPath = document.RootElement.GetProperty("content").GetString()
                ?? throw new InvalidDataException("Save journal manifest has no content path.");
            var contentBytes = await File.ReadAllBytesAsync(ResolveRelativePath(journal, relativeContentPath), cancellationToken);
            await WriteFileSafelyAsync(ResolveRelativePath(root, relativeContentPath), contentBytes, cancellationToken);
            await WriteFileSafelyAsync(Path.Combine(root, ForgeProjectWorkspace.ManifestFileName), manifestBytes, cancellationToken);
            DeleteDirectory(journal);
        }
        if (!Directory.Exists(recoveryRoot)) return;
        foreach (var directory in Directory.EnumerateDirectories(recoveryRoot, ".save-journal-*")) DeleteDirectory(directory);
    }

    private static async Task<IReadOnlyList<ProjectRecoverySnapshot>> TrimRecoveriesAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var snapshots = await ListRecoveriesAsync(root, cancellationToken);
        long retainedBytes = 0;
        var retained = new List<ProjectRecoverySnapshot>();
        foreach (var snapshot in snapshots)
        {
            if (retained.Count >= MaxRecoverySnapshots || snapshot.Size > MaxRecoveryBytes - retainedBytes)
            {
                DeleteDirectory(Path.Combine(root, RecoveryDirectoryName, snapshot.Id));
                continue;
            }
            retained.Add(snapshot);
            retainedBytes += snapshot.Size;
        }
        return retained;
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    internal static async Task WriteFileSafelyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.partial";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string Fingerprint(byte[] manifest, byte[] content)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("HorizonForgeProjectSnapshot\0"u8);
        hash.AppendData(manifest);
        hash.AppendData(content);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool TryParseRecoveryId(string value, out long created)
    {
        created = 0;
        var separator = value.IndexOf('-');
        return separator > 0
            && long.TryParse(value.AsSpan(0, separator), out created)
            && created >= 0
            && value.Length == separator + 33
            && value.AsSpan(separator + 1).ToString().All(Uri.IsHexDigit);
    }

    internal static bool TryGetRecoveryCreated(string? value, out long created)
    {
        if (value is not null) return TryParseRecoveryId(value, out created);
        created = 0;
        return false;
    }

    private static long DirectorySize(string path) =>
        Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);

    private static bool SamePath(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

}

internal sealed record LoadedProject(
    ForgeProjectManifest Manifest,
    ForgeProjectContent Content);

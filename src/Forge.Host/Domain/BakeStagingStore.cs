using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Forge.Host.Domain;

public sealed class BakeStagingStore
{
    public const string StagingDirectoryName = "staging";
    public const string ManifestFileName = "bake-manifest.json";
    private readonly SemaphoreSlim _commitGate = new(1, 1);

    private BakeStagingStore(string rootPath, BakeManifest manifest)
    {
        RootPath = rootPath;
        Manifest = manifest;
    }

    public string RootPath { get; }
    public BakeManifest Manifest { get; private set; }

    public static async Task<BakeStagingStore> OpenAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(Path.GetFullPath(projectRoot), StagingDirectoryName);
        var manifestPath = Path.Combine(root, ManifestFileName);
        if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Staging directory cannot be a symbolic link.");
        if (!File.Exists(manifestPath))
            return new(root, new(BakeSchema.CurrentVersion, BakeSchema.ManifestDocumentType, []));

        var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        var manifest = ForgeProjectPersistence.Deserialize<BakeManifest>(bytes, "Bake manifest");
        await ValidateManifestAsync(root, manifest, cancellationToken);
        return new(root, manifest);
    }

    public async Task<BakeLayerSnapshot> CommitAsync(
        BakeLayerPlan plan,
        Func<string, CancellationToken, Task> writeOutput,
        CancellationToken cancellationToken = default)
        => await CommitAsync(plan, writeOutput, null, cancellationToken);

    public async Task<BakeLayerSnapshot> CommitAsync(
        BakeLayerPlan plan,
        Func<string, CancellationToken, Task> writeOutput,
        Func<string, CancellationToken, Task>? validateOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(writeOutput);
        if (plan.State == BakeLayerState.Blocked) throw new InvalidOperationException($"Layer {plan.Layer} is blocked.");
        ValidateFingerprint(plan.ContentFingerprint, "content");
        ValidateFingerprint(plan.InputFingerprint, "input");

        await _commitGate.WaitAsync(cancellationToken);
        var temporary = Path.Combine(RootPath, $".write-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporary);
            await writeOutput(temporary, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (validateOutput is not null) await validateOutput(temporary, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var (outputFingerprint, size) = await FingerprintDirectoryAsync(temporary, cancellationToken);
            var relativePath = $"layers/{plan.Layer.ToString().ToLowerInvariant()}/{plan.InputFingerprint}";
            var destination = ForgeProjectPersistence.ResolveRelativePath(RootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(destination))
            {
                var existing = await FingerprintDirectoryAsync(destination, cancellationToken);
                if (existing.Fingerprint != outputFingerprint || existing.Size != size)
                    throw new InvalidDataException($"Layer {plan.Layer} produced different output for the same input fingerprint.");
            }
            else
            {
                Directory.Move(temporary, destination);
            }

            var snapshot = new BakeLayerSnapshot(
                plan.Layer,
                plan.ContentFingerprint,
                plan.InputFingerprint,
                outputFingerprint,
                relativePath,
                size);
            var layers = Manifest.Layers.Where(value => value.Layer != plan.Layer)
                .Append(snapshot)
                .OrderBy(value => value.Layer)
                .ToArray();
            var paletteReport = BakeSchema.UsesSharedPalette(plan.Layer) ? null : Manifest.PaletteReport;
            var next = new BakeManifest(BakeSchema.CurrentVersion, BakeSchema.ManifestDocumentType, layers, paletteReport);
            await ForgeProjectPersistence.WriteFileSafelyAsync(
                Path.Combine(RootPath, ManifestFileName),
                ForgeProjectPersistence.Serialize(next),
                cancellationToken);
            Manifest = next;
            return snapshot;
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
            _commitGate.Release();
        }
    }

    internal async Task RestoreManifestAsync(
        BakeManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        await _commitGate.WaitAsync(cancellationToken);
        try
        {
            await ValidateManifestAsync(RootPath, manifest, cancellationToken);
            await ForgeProjectPersistence.WriteFileSafelyAsync(
                Path.Combine(RootPath, ManifestFileName),
                ForgeProjectPersistence.Serialize(manifest),
                cancellationToken);
            Manifest = manifest;
        }
        finally
        {
            _commitGate.Release();
        }
    }

    internal async Task SetPaletteReportAsync(
        PaletteBakeReport report,
        CancellationToken cancellationToken = default)
    {
        PaletteBakeReportService.ValidateForCommit(report);
        await _commitGate.WaitAsync(cancellationToken);
        try
        {
            var next = Manifest with { PaletteReport = report };
            await ForgeProjectPersistence.WriteFileSafelyAsync(
                Path.Combine(RootPath, ManifestFileName),
                ForgeProjectPersistence.Serialize(next),
                cancellationToken);
            Manifest = next;
        }
        finally
        {
            _commitGate.Release();
        }
    }

    private static async Task ValidateManifestAsync(
        string root,
        BakeManifest manifest,
        CancellationToken cancellationToken)
    {
        if (manifest.SchemaVersion != BakeSchema.CurrentVersion || manifest.DocumentType != BakeSchema.ManifestDocumentType)
            throw new InvalidDataException("Bake manifest schema is unsupported.");
        if (manifest.Layers.Select(value => value.Layer).Distinct().Count() != manifest.Layers.Count)
            throw new InvalidDataException("Bake manifest contains duplicate layers.");
        if (manifest.PaletteReport is not null)
            PaletteBakeReportService.ValidateForCommit(manifest.PaletteReport);
        foreach (var snapshot in manifest.Layers)
        {
            ValidateFingerprint(snapshot.ContentFingerprint, "content");
            ValidateFingerprint(snapshot.InputFingerprint, "input");
            ValidateFingerprint(snapshot.OutputFingerprint, "output");
            var expectedPath = $"layers/{snapshot.Layer.ToString().ToLowerInvariant()}/{snapshot.InputFingerprint}";
            if (snapshot.RelativePath != expectedPath)
                throw new InvalidDataException($"Staged layer {snapshot.Layer} path is invalid.");
            var path = ForgeProjectPersistence.ResolveRelativePath(root, snapshot.RelativePath);
            if (!Directory.Exists(path)) throw new InvalidDataException($"Staged layer {snapshot.Layer} is missing.");
            var actual = await FingerprintDirectoryAsync(path, cancellationToken);
            if (actual.Fingerprint != snapshot.OutputFingerprint || actual.Size != snapshot.Size)
                throw new InvalidDataException($"Staged layer {snapshot.Layer} does not match its manifest.");
        }
    }

    private static async Task<(string Fingerprint, long Size)> FingerprintDirectoryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("HorizonForgeBakeOutput\0"u8);
        long size = 0;
        var files = EnumerateFilesSafely(path)
            .Select(file => (Path: file, Relative: Path.GetRelativePath(path, file).Replace(Path.DirectorySeparatorChar, '/')))
            .OrderBy(value => value.Relative, StringComparer.Ordinal);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Relative.Contains('\\')) throw new InvalidDataException("Staged output paths must be portable.");
            AppendString(hash, file.Relative);
            var info = new FileInfo(file.Path);
            AppendLong(hash, info.Length);
            size = checked(size + info.Length);
            await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0) hash.AppendData(buffer.AsSpan(0, read));
        }
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), size);
    }

    private static IEnumerable<string> EnumerateFilesSafely(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Staged output cannot be a symbolic link.");
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Staged output cannot contain symbolic links.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else yield return path;
            }
        }
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void AppendLong(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void ValidateFingerprint(string value, string description)
    {
        if (!BakeSchema.IsFingerprint(value))
            throw new InvalidDataException($"Bake {description} fingerprint is invalid.");
    }
}

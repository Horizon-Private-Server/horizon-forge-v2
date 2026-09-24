using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Forge.Host.Domain;

public static class OpaqueContentStore
{
    public const string RelativeRootPath = "content/opaque";
    public const string ManifestFileName = "manifest.json";

    internal static async Task WriteAsync(
        string projectRoot,
        OpaqueContentSource source,
        IReadOnlyList<OpaqueSectionCapture> captures,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(captures);
        ValidateSource(source);
        var opaqueRoot = ForgeProjectPersistence.ResolveRelativePath(projectRoot, RelativeRootPath);
        var blobRoot = Path.Combine(opaqueRoot, "blobs");
        RejectSymbolicLink(opaqueRoot);
        RejectSymbolicLink(blobRoot);
        Directory.CreateDirectory(blobRoot);

        var sections = new List<OpaqueSectionRecord>(captures.Count);
        foreach (var capture in captures.OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCapture(capture);
            var checksum = Convert.ToHexString(SHA256.HashData(capture.Bytes)).ToLowerInvariant();
            var id = SectionId(source, capture.Name, capture.Placement);
            var blob = $"blobs/{checksum}.bin";
            await ForgeProjectPersistence.WriteFileSafelyAsync(
                ForgeProjectPersistence.ResolveRelativePath(opaqueRoot, blob),
                capture.Bytes,
                cancellationToken);
            sections.Add(new(id, capture.Name, capture.Placement, checksum, capture.Bytes.LongLength, blob));
        }

        var manifest = new OpaqueContentManifest(
            OpaqueContentSchema.CurrentVersion,
            OpaqueContentSchema.ManifestDocumentType,
            source with { Fingerprint = source.Fingerprint.ToLowerInvariant() },
            sections.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray());
        await ForgeProjectPersistence.WriteFileSafelyAsync(
            Path.Combine(opaqueRoot, ManifestFileName),
            ForgeProjectPersistence.Serialize(manifest),
            cancellationToken);
    }

    public static async Task<OpaqueContentInspection> InspectAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        var opaqueRoot = ForgeProjectPersistence.ResolveRelativePath(projectRoot, RelativeRootPath);
        var manifestPath = Path.Combine(opaqueRoot, ManifestFileName);
        if (!File.Exists(manifestPath)) return Missing("Opaque content manifest is missing");
        try
        {
            RejectSymbolicLink(opaqueRoot);
            RejectSymbolicLink(Path.Combine(opaqueRoot, "blobs"));
            RejectSymbolicLink(manifestPath);
            var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
            var manifest = ForgeProjectPersistence.Deserialize<OpaqueContentManifest>(bytes, "Opaque content manifest");
            var blockers = ValidateManifest(manifest).ToList();
            foreach (var section in manifest.Sections ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (section is null) continue;
                if (!BakeSchema.IsFingerprint(section.Checksum) || !BakeSchema.IsFingerprint(section.Id)) continue;
                var expectedBlob = $"blobs/{section.Checksum}.bin";
                if (section.Blob != expectedBlob) continue;
                var path = ForgeProjectPersistence.ResolveRelativePath(opaqueRoot, section.Blob);
                if (!File.Exists(path))
                {
                    blockers.Add($"Opaque section {section.Name} is missing; re-import it from the verified source ISO.");
                    continue;
                }
                RejectSymbolicLink(path);
                var info = new FileInfo(path);
                if (info.Length != section.Size)
                {
                    blockers.Add($"Opaque section {section.Name} has changed size; re-import it from the verified source ISO.");
                    continue;
                }
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var checksum = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
                if (checksum != section.Checksum)
                    blockers.Add($"Opaque section {section.Name} checksum changed; re-import it from the verified source ISO.");
            }
            return new(manifest, blockers);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return Missing($"Opaque content manifest is invalid ({exception.Message})");
        }
    }

    public static async Task<BakeLayerInput> CreateBakeInputAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        var inspection = await InspectAsync(projectRoot, cancellationToken);
        return new(
            BakeLayerId.Opaque,
            inspection.Manifest is null ? ReadOnlyMemory<byte>.Empty : ForgeProjectPersistence.Serialize(inspection.Manifest),
            [],
            ReadOnlyMemory<byte>.Empty,
            inspection.Blockers);
    }

    public static async Task<BakeLayerSnapshot> StageAsync(
        string projectRoot,
        BakeStagingStore staging,
        BakeLayerPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Layer != BakeLayerId.Opaque) throw new ArgumentException("Opaque staging requires the opaque layer plan.", nameof(plan));
        var inspection = await InspectAsync(projectRoot, cancellationToken);
        if (!inspection.IsValid)
            throw new InvalidDataException(string.Join(' ', inspection.Blockers));
        var opaqueRoot = ForgeProjectPersistence.ResolveRelativePath(projectRoot, RelativeRootPath);
        var manifest = inspection.Manifest!;
        var manifestBytes = ForgeProjectPersistence.Serialize(manifest);
        return await staging.CommitAsync(plan, async (output, token) =>
        {
            await ForgeProjectPersistence.WriteFileSafelyAsync(
                Path.Combine(output, ManifestFileName),
                manifestBytes,
                token);
            foreach (var section in manifest.Sections.DistinctBy(value => value.Blob))
            {
                var source = ForgeProjectPersistence.ResolveRelativePath(opaqueRoot, section.Blob);
                var destination = ForgeProjectPersistence.ResolveRelativePath(output, section.Blob);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await CopyFileAsync(source, destination, token);
            }
        }, async (output, token) =>
        {
            if (!(await File.ReadAllBytesAsync(Path.Combine(output, ManifestFileName), token)).SequenceEqual(manifestBytes))
                throw new InvalidDataException("Opaque staged manifest changed during write.");
            foreach (var section in manifest.Sections)
            {
                var bytes = await File.ReadAllBytesAsync(
                    ForgeProjectPersistence.ResolveRelativePath(output, section.Blob), token);
                if (bytes.LongLength != section.Size
                    || Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != section.Checksum)
                    throw new InvalidDataException($"Opaque staged section {section.Name} failed size or checksum validation.");
            }
        }, cancellationToken);
    }

    private static IReadOnlyList<string> ValidateManifest(OpaqueContentManifest manifest)
    {
        var blockers = new List<string>();
        if (manifest.SchemaVersion != OpaqueContentSchema.CurrentVersion
            || manifest.DocumentType != OpaqueContentSchema.ManifestDocumentType)
            blockers.Add("Opaque content manifest schema is unsupported; re-import it from the verified source ISO.");
        try { ValidateSource(manifest.Source); }
        catch (ArgumentException)
        {
            blockers.Add("Opaque content source identity is invalid; re-import it from the verified source ISO.");
        }
        if (manifest.Sections is null)
        {
            blockers.Add("Opaque content section list is missing; re-import it from the verified source ISO.");
            return blockers;
        }
        if (manifest.Sections.Any(value => value is null)
            || manifest.Sections.Where(value => value is not null).Select(value => value.Id)
                .Distinct(StringComparer.Ordinal).Count() != manifest.Sections.Count)
            blockers.Add("Opaque content manifest contains duplicate section IDs; re-import it from the verified source ISO.");
        foreach (var section in manifest.Sections)
        {
            if (section is null) continue;
            if (string.IsNullOrWhiteSpace(section.Name)
                || string.IsNullOrWhiteSpace(section.Placement?.Container)
                || section.Placement.HeaderOffset < 0
                || !BakeSchema.IsFingerprint(section.Id)
                || !BakeSchema.IsFingerprint(section.Checksum)
                || section.Size <= 0
                || section.Placement.Offset < 0
                || section.Placement.Length != section.Size
                || section.Placement.Alignment <= 0
                || section.Blob != $"blobs/{section.Checksum}.bin"
                || (manifest.Source is not null && section.Id != SectionId(manifest.Source, section.Name, section.Placement)))
                blockers.Add($"Opaque section {section.Name} metadata is invalid; re-import it from the verified source ISO.");
        }
        return blockers;
    }

    private static void ValidateSource(OpaqueContentSource source)
    {
        if (source is null
            || new[] { source.Game, source.Region, source.Revision, source.Fingerprint }.Any(string.IsNullOrWhiteSpace)
            || source.Level < 0
            || source.Fingerprint.Length != 32
            || source.Fingerprint.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Opaque content source identity is invalid.", nameof(source));
    }

    private static void ValidateCapture(OpaqueSectionCapture capture)
    {
        if (string.IsNullOrWhiteSpace(capture.Name)
            || string.IsNullOrWhiteSpace(capture.Placement.Container)
            || capture.Placement.HeaderOffset < 0
            || capture.Placement.Offset < 0
            || capture.Placement.Length != capture.Bytes.LongLength
            || capture.Placement.Alignment <= 0
            || capture.Bytes.Length == 0)
            throw new ArgumentException($"Opaque section {capture.Name} is invalid.", nameof(capture));
    }

    private static string SectionId(OpaqueContentSource source, string name, OpaqueSectionPlacement placement)
    {
        var identity = string.Create(CultureInfo.InvariantCulture,
            $"{source.Game}\0{source.Region}\0{source.Revision}\0{source.Level}\0{source.Fingerprint.ToLowerInvariant()}\0{name}\0{placement.Container}\0{placement.HeaderOffset}\0{placement.Offset}\0{placement.Length}\0{placement.Alignment}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"HorizonForgeOpaqueSection\0{identity}")))
            .ToLowerInvariant();
    }

    private static OpaqueContentInspection Missing(string message) =>
        new(null, [$"{message}; re-import it from the verified source ISO."]);

    private static void RejectSymbolicLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path))
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Opaque content cannot use symbolic links.");
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }
}

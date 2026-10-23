using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal sealed record UyaContentImportSourceSnapshot(
    UyaIsoIdentity Identity,
    AssetCatalogStore Catalog,
    UyaBaseLevelData Source,
    IReadOnlyList<UyaContentImportSourceFamily> Families,
    IReadOnlyList<UyaMobyExternalReferenceRequirement> ExternalReferences,
    IReadOnlyList<string> Warnings);

internal static class UyaContentImportSourceCache
{
    private const string CatalogFileName = "catalog-v0.json";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IdentityCacheEntry? _identityEntry;
    private static SourceCacheEntry? _sourceEntry;

    public static async Task<UyaContentImportSourceSnapshot> ReadAsync(
        UyaContentImportPlanRequest request,
        string targetFingerprint,
        Func<IsoProgress, ValueTask>? progress,
        CancellationToken cancellationToken)
    {
        var discVersion = CaptureDiscVersion(request.SourceIsoPath);
        var sourceVersion = CaptureSourceVersion(request, discVersion);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (_sourceEntry is { } cached && cached.Version == sourceVersion)
            {
                UyaContentImportPlanningService.ValidateSourceIdentity(
                    cached.Snapshot.Identity, request.SourceFingerprint, targetFingerprint);
                if (progress is not null) await progress(new(9_000, 10_000));
                return cached.Snapshot;
            }

            UyaIsoIdentity identity;
            if (_identityEntry is { } verified && verified.Version == discVersion)
            {
                identity = verified.Identity;
                if (progress is not null) await progress(new(7_000, 10_000));
            }
            else
            {
                identity = await UyaIsoService.ValidateAsync(
                    request.SourceIsoPath,
                    progress is null ? null : value => progress(new(
                        value.Total == 0 ? 0 : value.Completed * 7_000 / value.Total,
                        10_000)),
                    cancellationToken);
                if (discVersion != CaptureDiscVersion(request.SourceIsoPath))
                    throw new IOException("The donor ISO changed while it was being verified; retry the import.");
                _identityEntry = new(discVersion, identity);
            }
            UyaContentImportPlanningService.ValidateSourceIdentity(
                identity, request.SourceFingerprint, targetFingerprint);

            cancellationToken.ThrowIfCancellationRequested();
            if (progress is not null) await progress(new(7_500, 10_000));
            var catalog = await AssetCatalogStore.OpenReadOnlyAsync(request.CatalogRootPath, cancellationToken);
            await using var iso = new FileStream(
                request.SourceIsoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            var source = UyaBaseLevelService.Read(
                iso, catalog, request.SourceLevel, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (sourceVersion != CaptureSourceVersion(request, CaptureDiscVersion(request.SourceIsoPath)))
                throw new IOException("The donor ISO or asset catalog changed while it was being read; retry the import.");

            var externalReferences = UyaMobyContentImportService.ExternalReferenceRequirements(source);
            var snapshot = new UyaContentImportSourceSnapshot(
                identity,
                catalog,
                source,
                UyaContentImportPlanningService.Inventory(source),
                externalReferences,
                UyaContentImportPlanningService.SourceWarnings(source, externalReferences.Count));
            _sourceEntry = new(sourceVersion, snapshot);
            if (progress is not null) await progress(new(9_000, 10_000));
            return snapshot;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static DiscVersion CaptureDiscVersion(string sourceIsoPath)
    {
        var isoPath = Path.GetFullPath(sourceIsoPath);
        var iso = new FileInfo(isoPath);
        if (!iso.Exists) throw new FileNotFoundException("The donor ISO does not exist.", isoPath);
        return new(isoPath, iso.Length, iso.LastWriteTimeUtc.Ticks);
    }

    private static SourceVersion CaptureSourceVersion(
        UyaContentImportPlanRequest request,
        DiscVersion discVersion)
    {
        var catalogPath = Path.Combine(Path.GetFullPath(request.CatalogRootPath), CatalogFileName);
        var catalog = new FileInfo(catalogPath);
        return new(
            discVersion,
            catalogPath,
            catalog.Exists ? catalog.Length : -1,
            catalog.Exists ? catalog.LastWriteTimeUtc.Ticks : 0,
            request.SourceLevel);
    }

    private sealed record DiscVersion(
        string IsoPath,
        long IsoLength,
        long IsoLastWriteTicks);

    private sealed record SourceVersion(
        DiscVersion Disc,
        string CatalogPath,
        long CatalogLength,
        long CatalogLastWriteTicks,
        int SourceLevel);

    private sealed record IdentityCacheEntry(
        DiscVersion Version,
        UyaIsoIdentity Identity);

    private sealed record SourceCacheEntry(
        SourceVersion Version,
        UyaContentImportSourceSnapshot Snapshot);
}

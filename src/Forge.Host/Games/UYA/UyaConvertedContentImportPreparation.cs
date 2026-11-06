using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal sealed class UyaConvertedContentImportPreparation
{
    public AssetCatalogStore Catalog { get; }
    public UyaBaseLevelData Source { get; }
    public byte[] RawGameplay { get; }
    public IReadOnlyDictionary<string, string> Diagnostics { get; }
    public IReadOnlyDictionary<int, IReadOnlyList<int>> SplitTieClasses { get; }
    public IReadOnlyList<string> Warnings { get; }

    private UyaConvertedContentImportPreparation(AssetCatalogStore catalog, UyaBaseLevelData source,
        byte[] rawGameplay, IReadOnlyDictionary<string, string> diagnostics,
        IReadOnlyDictionary<int, IReadOnlyList<int>> splitTieClasses, string sourceGame)
    {
        Catalog = catalog; Source = source; RawGameplay = rawGameplay; Diagnostics = diagnostics;
        SplitTieClasses = splitTieClasses;
        Warnings = splitTieClasses.OrderBy(pair => pair.Key).Select(pair =>
            $"{sourceGame} Tie 0x{pair.Key:X4} is split into {pair.Value.Count} native Ties per placement to retain all geometry and LODs within UYA limits.").ToArray();
    }

    public static async Task<UyaConvertedContentImportPreparation> CreateAsync(
        UyaConvertedContentImportPlanRequest request, ContentImportSource rawSource,
        MobyDexCatalog mobyDex, Func<IsoProgress, ValueTask>? progress, CancellationToken cancellationToken)
    {
        var wad = rawSource.LevelWad ?? throw new InvalidDataException("The donor archive is unavailable.");
        var profile = UyaContentImportDonorProfile.For(request.SourceGame);
        var importerVersion = $"forge-{request.SourceGame.ToString().ToLowerInvariant()}-content-v1";
        var originals = await AssetCatalogStore.OpenReadOnlyAsync(request.CatalogRootPath, cancellationToken);
        var catalog = await AssetCatalogStore.CreateTransientAsync(originals,
            await UyaBaseLayerStore.ReadAssetIdsAsync(request.ProjectPath, cancellationToken),
            ForgeProjectWorkspace.MaxAttachedAssetBytes, cancellationToken);
        var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal);
        var conversion = rawSource.Conversion ?? await Task.Run(
            () => LevelContentConverter.Convert(request.SourceGame, GameId.UYA, wad, cancellationToken), cancellationToken);
        var entries = ReadEntries(originals, request, profile.Revision, cancellationToken).ToArray();
        var classes = rawSource.Families.ToDictionary(family => family.Family,
            family => family.Items.Select(item => item.ClassId).ToHashSet(), StringComparer.Ordinal);
        var usesByKind = conversion.Models.GroupBy(use => use.Kind).ToDictionary(group => group.Key, group => group.ToArray());
        var assets = new List<AssetCatalogPut>();
        var splitTies = new Dictionary<int, IReadOnlyList<int>>();
        var occupiedTies = conversion.Models.Where(use => use.Kind == FrontendAssetKind.Tie)
            .Select(use => use.ClassId).Concat(classes["ties"].OfType<int>()).ToHashSet();
        var completed = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sources = entry.Sources.Where(value => Matches(value, request, profile.Revision)).ToArray();
            if (sources.Length == 0) continue;
            var family = entry.Kind switch { AssetKind.Tie => "ties", AssetKind.Shrub => "shrubs", _ => "mobys" };
            try
            {
                if (entry.Kind is AssetKind.Tie or AssetKind.Shrub or AssetKind.Moby)
                {
                    var kind = Enum.Parse<FrontendAssetKind>(entry.Kind.ToString());
                    var indices = sources.Select(source => source.SourceIndex).ToHashSet();
                    var uses = (usesByKind.GetValueOrDefault(kind) ?? []).Where(use =>
                        classes[family].Contains(use.ClassId) && indices.Contains(use.SourceIndex)).ToArray();
                    if (uses.Length == 0) continue;
                    var model = await UyaConvertedModelService.ReadAsync(originals, entry, uses[0].ClassId, cancellationToken);
                    if (model.Model.Game != request.SourceGame)
                        throw new InvalidDataException($"The {request.SourceGame} catalog model has an invalid source game.");
                    foreach (var use in uses)
                    {
                        var parts = await UyaConvertedModelService.ConvertPartsAsync(model, entry.Kind, use.ClassId, cancellationToken);
                        if (parts.Count > 1 && entry.Kind != AssetKind.Tie)
                            throw new NotSupportedException("Only Tie model instance expansion is supported.");
                        if (parts.Count > 1 && !splitTies.ContainsKey(use.ClassId))
                        {
                            var ids = new List<int> { use.ClassId };
                            for (var part = 1; part < parts.Count; part++)
                            {
                                var id = Enumerable.Range(UyaCustomTieClassRegistry.FirstClassId,
                                    UyaCustomTieClassRegistry.LastClassId - UyaCustomTieClassRegistry.FirstClassId + 1)
                                    .FirstOrDefault(value => !occupiedTies.Contains(value), -1);
                                if (id < 0) throw new InvalidDataException("No native Tie class IDs remain for the split donor model.");
                                occupiedTies.Add(id); ids.Add(id);
                            }
                            splitTies.Add(use.ClassId, ids);
                        }
                        var idsForParts = splitTies.GetValueOrDefault(use.ClassId) ?? [use.ClassId];
                        if (idsForParts.Count != parts.Count)
                            throw new InvalidDataException("Donor Tie variants disagree on their split model count.");
                        var source = sources.First(value => value.SourceIndex == use.SourceIndex);
                        var alias = entry.Kind.ToString().ToLowerInvariant();
                        for (var part = 0; part < parts.Count; part++)
                        {
                            var id = idsForParts[part];
                            var native = part == 0 ? parts[part] : UyaConvertedModelService.RebaseTie(parts[part], id);
                            assets.Add(new(entry.Kind, UyaAssetImportService.CanonicalFormatVersion, native,
                                new(importerVersion, source with { Game = "UYA", Revision = "1.00" },
                                    [$"{alias}:{id}", $"{alias}:0x{id:X4}"])));
                        }
                    }
                }
                else
                {
                    var path = originals.ResolveBlobPath(entry.Id) ?? throw new FileNotFoundException($"{request.SourceGame} sound asset {entry.Id} is missing.");
                    var bytes = await AssetCatalogBlobReader.ReadVerifiedAsync(entry, path, ForgeProjectWorkspace.MaxAttachedAssetBytes, cancellationToken);
                    assets.Add(new(entry.Kind, entry.CanonicalFormatVersion, bytes,
                        new(importerVersion, sources[0] with { Game = "UYA", Revision = "1.00" }, entry.Aliases)));
                }
            }
            catch (Exception cause) when (cause is IOException or InvalidDataException or NotSupportedException or ArgumentException)
            {
                diagnostics.TryAdd(family, cause.Message.Length > 512 ? cause.Message[..512] : cause.Message);
            }
            if (progress is not null) await progress(new(9_000 + ++completed * 700L / Math.Max(1, entries.Length), 10_000));
        }
        await catalog.PutManyAsync(assets, cancellationToken);
        if (splitTies.Count > 0)
            conversion = await Task.Run(() => LevelContentConverter.ExpandTieModels(GameId.UYA,
                conversion, splitTies, cancellationToken), cancellationToken);
        var nativeSource = await Task.Run(() => UyaBaseLevelService.ReadConverted(wad, conversion,
            catalog, request.SourceLevel, mobyDex, request.SourceGame.ToString(), cancellationToken), cancellationToken);
        var generated = UyaBaseLayerService.CreateCatalogPuts(nativeSource.BaseLayers,
            new("UYA", "NTSC-U", "1.00", request.SourceLevel, request.SourceFingerprint), importerVersion).ToList();
        generated.AddRange((nativeSource.HudFxTextures ?? []).Select(bytes => new AssetCatalogPut(
            AssetKind.Texture, ProjectTextureAssetSchema.CanonicalFormatVersion, bytes,
            new(importerVersion, new("UYA", "NTSC-U", "1.00", $"level{request.SourceLevel:00}",
                "level_wad/level_data.wad", 0, request.SourceFingerprint)))));
        await catalog.PutManyAsync(generated, cancellationToken);
        foreach (var family in new[] { "ties", "shrubs", "mobys" })
            if (nativeSource.Entities.Any(entity => entity.Layer == family && entity.Asset is null && entity.Source?.ModelLess != true))
                diagnostics.TryAdd(family, $"{request.SourceGame} {family} have missing or unsupported model dependencies. Reimport {request.SourceGame} assets in Setup.");
        nativeSource = nativeSource with { CatalogRevision = originals.Revision };
        return new(catalog, nativeSource, conversion.RawGameplay, diagnostics, splitTies, request.SourceGame.ToString());
    }

    private static bool Matches(AssetSource source, UyaConvertedContentImportPlanRequest request, string revision) =>
        source.Game == request.SourceGame.ToString() && source.Region == "NTSC-U" && source.Revision == revision
        && source.Level == $"level{request.SourceLevel:00}"
        && (request.SourceGame != GameId.DL || !source.Archive.StartsWith("missions/", StringComparison.Ordinal))
        && source.Fingerprint.Equals(request.SourceFingerprint, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<AssetCatalogEntry> ReadEntries(AssetCatalogStore catalog, UyaConvertedContentImportPlanRequest request,
        string revision, CancellationToken token)
    {
        foreach (var kind in new[] { AssetKind.Tie, AssetKind.Shrub, AssetKind.Moby, AssetKind.SoundBank, AssetKind.Sound, AssetKind.SoundSample })
        {
            string? cursor = null;
            do
            {
                var page = catalog.QueryPage(new(kind, Game: request.SourceGame.ToString(), Level: $"level{request.SourceLevel:00}", Region: "NTSC-U",
                    Revision: revision, Cursor: cursor, Limit: AssetCatalogStore.MaxPageLimit), token);
                foreach (var entry in page.Entries) yield return entry;
                cursor = page.NextCursor;
            } while (cursor is not null);
        }
    }
}

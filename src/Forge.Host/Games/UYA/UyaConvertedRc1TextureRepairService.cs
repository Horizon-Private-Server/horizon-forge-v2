using Forge.Host.Domain;
using Forge.Host.Games.RC1;
using RatchetPs2.Core.Games;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaConvertedRc1TextureRepairService
{
    internal static async Task<CatalogModel?> FindUpdatedSourceAsync(AssetCatalogStore catalog,
        AssetCatalogEntry entry, CatalogModel original, int classId, CancellationToken token)
    {
        if (original.Game != GameId.RC1 || original.Textures.Count == 0
            || entry.ImporterVersion.StartsWith(Rc1AssetImportService.ImporterVersionPrefix, StringComparison.Ordinal)) return null;
        var visited = new HashSet<AssetId> { entry.Id };
        foreach (var level in entry.Sources.Where(source => source.Game == "RC1").Select(source => source.Level).Distinct())
        {
            string? cursor = null;
            do
            {
                token.ThrowIfCancellationRequested();
                var page = catalog.QueryPage(new(entry.Kind, Search: $"0x{classId:X4}", Game: "RC1", Level: level,
                    Cursor: cursor, Limit: AssetCatalogStore.MaxPageLimit), token);
                foreach (var candidate in page.Entries)
                {
                    if (!visited.Add(candidate.Id)
                        || candidate.CanonicalFormatVersion != entry.CanonicalFormatVersion
                        || !candidate.ImporterVersion.StartsWith(Rc1AssetImportService.ImporterVersionPrefix, StringComparison.Ordinal)
                        || !candidate.Sources.Any(source => entry.Sources.Any(old => AssetSourceSelection.From(old).Matches(source)
                            && old.Fingerprint == source.Fingerprint))) continue;
                    var path = catalog.ResolveBlobPath(candidate.Id);
                    if (path is null) continue;
                    var bytes = await AssetCatalogBlobReader.ReadVerifiedAsync(candidate.Id, candidate.Kind,
                        candidate.CanonicalFormatVersion, candidate.Size, path, ForgeProjectWorkspace.MaxAttachedAssetBytes, token);
                    var updated = CatalogModelCodec.Decode(bytes);
                    if (await Task.Run(() => MatchesBase(original, updated), token)) return updated;
                }
                cursor = page.NextCursor;
            } while (cursor is not null);
        }
        return null;
    }

    private static bool MatchesBase(CatalogModel original, CatalogModel updated)
    {
        if (updated.Game != original.Game || !updated.DefinitionBytes.AsSpan().SequenceEqual(original.DefinitionBytes)
            || !updated.ModelBytes.AsSpan().SequenceEqual(original.ModelBytes) || updated.Textures.Count != original.Textures.Count) return false;
        return LevelAssetModelConverter.MatchesTextureBaseUpdate(original.Game, original.Textures, updated.Textures);
    }

    public static async Task<EditorDiagnostic?> HydrateAsync(ForgeProjectWorkspace workspace, AssetCatalogStore catalog, CancellationToken token)
    {
        if (workspace.Manifest.Target.Game != "UYA") return null;
        var overrides = workspace.Content.AssetOverrides.ToDictionary(binding => binding.Source);
        var attachments = new ProjectAssetAttachmentCollector();
        var repaired = 0;
        var needsImport = false;
        var warnings = new List<string>();
        foreach (var allocation in workspace.Content.CustomClasses.Allocations.Where(allocation =>
            allocation.Provenance.Origin == ProjectCustomClassOrigin.ConvertedAsset))
        {
            token.ThrowIfCancellationRequested();
            var entry = catalog.Query(new(Id: allocation.Asset.Id)).SingleOrDefault();
            if (entry is null || !entry.Sources.Any(source => source.Game == "RC1")
                || !overrides.TryGetValue(allocation.Asset, out var binding)) continue;
            try
            {
                var original = CatalogModelCodec.Decode(await workspace.ReadAttachedAssetVerifiedAsync(
                    allocation.Asset, ForgeProjectWorkspace.MaxAttachedAssetBytes, token));
                var updated = await FindUpdatedSourceAsync(catalog, entry, original, allocation.ClassId, token);
                if (updated is null)
                {
                    needsImport |= entry.ImporterVersion.StartsWith("forge-rc1-v1+", StringComparison.Ordinal) && original.Textures.Count > 0;
                    continue;
                }
                var native = UyaCanonicalAssetCodec.Decode(await workspace.ReadAttachedAssetVerifiedAsync(
                    binding.Replacement, ForgeProjectWorkspace.MaxAttachedAssetBytes, token));
                // Shared native blobs can have another parent; RepairTextures verifies the actual texture bytes.
                var bytes = await Task.Run(() => RepairTextures(original, updated, native, token), token);
                if (bytes is null) continue;
                var id = AssetId.Compute(allocation.Asset.Kind, UyaAssetImportService.CanonicalFormatVersion, bytes);
                var existing = workspace.Content.Assets.SingleOrDefault(asset => asset.Id == id);
                var replacement = attachments.Attach(allocation.Asset.Kind, UyaAssetImportService.CanonicalFormatVersion,
                    bytes, existing is null ? allocation.Asset.Id : existing.ParentId);
                overrides[allocation.Asset] = binding with { Replacement = replacement };
                repaired++;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException)
            {
                warnings.Add($"RC1 class 0x{allocation.ClassId:X4} textures were preserved ({error.Message}).");
            }
        }
        if (repaired > 0) await workspace.CommitAssetsAsync(workspace.Content with { AssetOverrides = overrides.Values.ToArray() }, attachments.Attachments, token);
        if (needsImport) warnings.Add("Re-import RC1 assets in Setup, then reopen this project to restore authored mipmaps.");
        return repaired == 0 && warnings.Count == 0 ? null : new("rc1-assets.mipmaps-repaired",
            warnings.Count == 0 ? EditorDiagnosticSeverity.Info : EditorDiagnosticSeverity.Warning,
            $"Restored RC1 mipmaps for {repaired} asset(s). Save and rebuild occlusion and the map. " + string.Join(' ', warnings));
    }

    private static byte[]? RepairTextures(CatalogModel source, CatalogModel updated, CatalogModel native, CancellationToken token)
    {
        var textures = LevelAssetModelConverter.RepairTextureUpdate(GameId.RC1, GameId.UYA,
            source.Textures, updated.Textures, native.Textures, token);
        return textures is null ? null : UyaCanonicalAssetCodec.Encode(native.DefinitionBytes, native.ModelBytes, textures);
    }
}

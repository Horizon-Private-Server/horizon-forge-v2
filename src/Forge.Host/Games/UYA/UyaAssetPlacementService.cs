using Forge.Host.Domain;
using RatchetPs2.Core.Ties;
using RatchetPs2.Core.Games;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.Games.UYA;

public static class UyaAssetPlacementService
{
    internal static (bool CanPlace, string? DisabledReason) PlacementCompatibility(
        AssetCatalogEntry entry,
        string targetRegion,
        string targetRevision,
        IReadOnlyList<int> classIds,
        bool blobAvailable)
    {
        if (entry.Kind is not (AssetKind.Tie or AssetKind.Shrub or AssetKind.Moby))
            return (false, $"{entry.Kind}s are preview-only.");
        if (!blobAvailable) return (false, "The catalog blob is missing.");
        var native = entry.Sources.Any(source => source.Game == "UYA"
            && source.Region == targetRegion && source.Revision == targetRevision);
        var convertible = targetRegion == "NTSC-U" && targetRevision == "1.00"
            && entry.CanonicalFormatVersion == UyaAssetImportService.CanonicalFormatVersion
            && entry.Tags.Contains("vanilla", StringComparer.Ordinal)
            && !entry.Tags.Contains("texture:placeholder", StringComparer.Ordinal)
            && entry.Sources.Any(source => source is { Game: "DL", Region: "NTSC-U", Revision: "1.00" });
        if (!native && !convertible)
            return (false, $"The asset is not compatible with UYA {targetRegion} {targetRevision}.");
        if (classIds.Count == 0) return (false, "The asset has no class identity for placement.");
        return (true, null);
    }

    public static async Task<ProjectEntity> CreateAsync(
        ForgeProjectWorkspace workspace,
        string catalogRootPath,
        EditorAssetPlacement placement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(placement);
        if (workspace.Manifest.Target is not { Game: "UYA", Region: "NTSC-U", Revision: "1.00" })
            throw new NotSupportedException("Asset placement currently supports UYA NTSC-U 1.00 projects only.");
        if (placement.Kind is not (AssetKind.Tie or AssetKind.Shrub or AssetKind.Moby))
            throw new ArgumentOutOfRangeException(nameof(placement), "Only ties, shrubs, and mobys can be placed.");
        if (placement.ClassId is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(placement), "Asset class ID is outside the UYA range.");
        placement.Source?.Validate();

        var custom = workspace.Content.CustomModels.Models.SingleOrDefault(
            model => model.Canonical.Id == placement.AssetId);
        if (custom is not null)
        {
            if (placement.Kind != AssetKind.Tie)
                throw new InvalidDataException("A project custom model can only be placed as a TIE.");
            var allocation = workspace.Content.CustomClasses.Allocations.Single(
                value => value.Asset == custom.Canonical);
            if (allocation.ClassId != placement.ClassId)
                throw new InvalidDataException("The custom TIE class allocation changed; refresh the asset selection.");
            var native = UyaCustomTieNativeCodec.Read(await workspace.ReadAttachedAssetVerifiedAsync(
                custom.Native, ForgeProjectWorkspace.MaxAttachedAssetBytes, cancellationToken));
            var ambientSize = TieClassReader.Read(native.ModelBytes, TieClassReadOptions.ForGameProfile(TieGameProfile.ForGame(GameId.UYA))).Header.AmbientSize;
            return CreateEntity(
                workspace,
                placement,
                custom.Canonical,
                UyaTieLightingDefaults.Create(workspace.Content.Entities, placement.Transform.Position, ambientSize),
                Path.GetFileNameWithoutExtension(custom.SourceName));
        }

        var catalog = await AssetCatalogStore.OpenReadOnlyAsync(catalogRootPath, cancellationToken);
        var entry = catalog.Query(new(Id: placement.AssetId)).SingleOrDefault()
            ?? throw new FileNotFoundException($"Asset {placement.AssetId} is not present in the catalog.");
        if (placement.Source is { } selected && !entry.Sources.Any(selected.Matches))
            throw new InvalidDataException("The selected source is not present in this catalog asset; refresh the asset selection.");
        if (entry.Kind != placement.Kind || entry.CanonicalFormatVersion != UyaAssetImportService.CanonicalFormatVersion
            || !entry.Tags.Contains("vanilla", StringComparer.Ordinal)
            || !AssetCatalogPaging.ClassIds(entry).Contains(placement.ClassId))
            throw new InvalidDataException("The catalog asset does not contain the selected class.");
        var converted = !entry.Sources.Any(source => source is { Game: "UYA", Region: "NTSC-U", Revision: "1.00" });
        if (placement.Source is { } source && (source.Game != (converted ? "DL" : "UYA")
            || source.Region != "NTSC-U" || source.Revision != "1.00"))
            throw new InvalidDataException("The selected asset source is not supported by this project.");
        if (converted)
            placement = placement with { ClassId = await UyaConvertedAssetPlacementService.PrepareAsync(
                workspace, catalog, entry, placement.ClassId, cancellationToken) };
        var bytes = await UyaInstancedCollisionCompositionService.ReadAssetAsync(
            workspace, catalog, entry.Id, entry.Kind, cancellationToken);
        var canonical = UyaCanonicalAssetCodec.Decode(bytes);
        if (canonical.DefinitionBytes.Length != (placement.Kind == AssetKind.Shrub ? 0x30 : 0x20))
            throw new InvalidDataException("The asset canonical definition does not match its kind.");

        var tieLighting = placement.Kind == AssetKind.Tie
            ? UyaTieLightingDefaults.Create(workspace.Content.Entities, placement.Transform.Position,
                TieClassReader.Read(canonical.ModelBytes, TieClassReadOptions.ForGameProfile(TieGameProfile.ForGame(GameId.UYA))).Header.AmbientSize)
            : null;
        var entity = CreateEntity(workspace, placement, new(entry.Id, entry.Kind), tieLighting);
        if (placement.Kind == AssetKind.Moby)
            await UyaMobySoundImportService.ImportAsync(
                workspace, catalog, entry, placement.ClassId, cancellationToken, placement.Source);
        return entity;
    }

    private static ProjectEntity CreateEntity(
        ForgeProjectWorkspace workspace,
        EditorAssetPlacement placement,
        ProjectAssetReference asset,
        ProjectTieLighting? tieLighting,
        string? name = null)
    {
        var layer = placement.Kind.ToString().ToLowerInvariant() + "s";
        var record = placement.Kind switch
        {
            AssetKind.Tie => CreateTieRecord(workspace, placement.ClassId),
            AssetKind.Shrub => UyaGameplayInstanceTemplates.CreateShrub(placement.ClassId),
            AssetKind.Moby => UyaGameplayInstanceTemplates.CreateMoby(
                placement.ClassId,
                NextValue(workspace, "mobys", UyaMobyInstancesReader.RecordSize,
                    value => UyaMobyInstancesReader.ReadInstance(value).Uid)),
            _ => throw new InvalidOperationException("Unsupported UYA placement kind."),
        };
        return new(
            EntityId.New(),
            name ?? $"{placement.Kind.ToString().ToLowerInvariant()}:0x{placement.ClassId:X4}",
            layer,
            placement.Transform,
            asset,
            Source: new(placement.ClassId, record),
            TieLighting: tieLighting);
    }

    internal static byte[] CreateTieRecord(ForgeProjectWorkspace workspace, int classId) =>
        UyaGameplayInstanceTemplates.CreateTie(
            classId,
            NextValue(workspace, "ties", UyaTieInstancesReader.RecordSize,
                value => UyaTieInstancesReader.ReadInstance(value).OcclusionId));

    private static int NextValue(
        ForgeProjectWorkspace workspace,
        string layer,
        int recordSize,
        Func<byte[], int> read)
    {
        var used = workspace.Content.Entities
            .Where(entity => entity.Layer == layer && entity.Source?.RawRecord.Length == recordSize)
            .Select(entity => read(entity.Source!.RawRecord))
            .Where(value => value >= 0)
            .ToHashSet();
        for (var value = 1; value < int.MaxValue; value++)
            if (!used.Contains(value)) return value;
        throw new InvalidDataException($"The UYA {layer} identifier space is exhausted.");
    }
}

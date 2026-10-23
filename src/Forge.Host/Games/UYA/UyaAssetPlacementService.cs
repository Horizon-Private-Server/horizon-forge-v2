using Forge.Host.Domain;
using RatchetPs2.Core.Ties;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.Games.UYA;

public static class UyaAssetPlacementService
{
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
            var ambientSize = TieClassReader.Read(native.ModelBytes).Header.AmbientSize;
            return CreateEntity(
                workspace,
                placement,
                custom.Canonical,
                UyaTieLightingDefaults.Create(workspace.Content.Entities, placement.Transform.Position, ambientSize),
                Path.GetFileNameWithoutExtension(custom.SourceName));
        }

        var catalog = await AssetCatalogStore.OpenAsync(catalogRootPath, cancellationToken);
        var entry = catalog.Query(new(Id: placement.AssetId)).SingleOrDefault()
            ?? throw new FileNotFoundException($"Asset {placement.AssetId} is not present in the catalog.");
        if (entry.Kind != placement.Kind || entry.CanonicalFormatVersion != UyaAssetImportService.CanonicalFormatVersion
            || !entry.Tags.Contains("vanilla", StringComparer.Ordinal)
            || !entry.Sources.Any(source => source is { Game: "UYA", Region: "NTSC-U", Revision: "1.00" })
            || !AssetCatalogPaging.ClassIds(entry).Contains(placement.ClassId))
            throw new InvalidDataException("The catalog asset does not contain the selected compatible UYA class.");
        var bytes = await UyaInstancedCollisionCompositionService.ReadAssetAsync(
            workspace, catalog, entry.Id, entry.Kind, cancellationToken);
        var canonical = UyaCanonicalAssetCodec.Decode(bytes);
        if (canonical.DefinitionBytes.Length != (placement.Kind == AssetKind.Shrub ? 0x30 : 0x20))
            throw new InvalidDataException("The asset canonical definition does not match its kind.");

        var tieLighting = placement.Kind == AssetKind.Tie
            ? UyaTieLightingDefaults.Create(workspace.Content.Entities, placement.Transform.Position,
                TieClassReader.Read(canonical.ModelBytes).Header.AmbientSize)
            : null;
        var entity = CreateEntity(workspace, placement, new(entry.Id, entry.Kind), tieLighting);
        if (placement.Kind == AssetKind.Moby)
            await UyaMobySoundImportService.ImportAsync(
                workspace, catalog, entry, placement.ClassId, cancellationToken);
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

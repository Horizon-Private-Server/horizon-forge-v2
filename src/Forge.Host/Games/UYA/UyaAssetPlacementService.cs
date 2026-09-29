using System.Buffers.Binary;
using Forge.Host.Domain;
using RatchetPs2.Core.Ties;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.Games.UYA;

public static class UyaAssetPlacementService
{
    internal const int DefaultTieDrawDistance = 4_000;
    internal const float DefaultShrubDrawDistance = 128f;

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

        var catalog = await AssetCatalogStore.OpenAsync(catalogRootPath, cancellationToken);
        var entry = catalog.Query(new(Id: placement.AssetId)).SingleOrDefault()
            ?? throw new FileNotFoundException($"Asset {placement.AssetId} is not present in the catalog.");
        if (entry.Kind != placement.Kind || entry.CanonicalFormatVersion != UyaAssetImportService.CanonicalFormatVersion
            || !entry.Tags.Contains("vanilla", StringComparer.Ordinal)
            || !entry.Sources.Any(source => source is { Game: "UYA", Region: "NTSC-U", Revision: "1.00" })
            || !AssetCatalogPaging.ClassIds(entry).Contains(placement.ClassId))
            throw new InvalidDataException("The catalog asset does not contain the selected compatible UYA class.");
        var path = workspace.ResolveAssetPath(entry.Id, catalog)
            ?? throw new FileNotFoundException($"Asset blob {entry.Id} is missing.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.LongLength != entry.Size
            || AssetId.Compute(entry.Kind, entry.CanonicalFormatVersion, bytes) != entry.Id)
            throw new InvalidDataException("The asset blob failed identity validation.");
        var canonical = UyaCanonicalAssetCodec.Decode(bytes);
        if (canonical.DefinitionBytes.Length != (placement.Kind == AssetKind.Shrub ? 0x30 : 0x20))
            throw new InvalidDataException("The asset canonical definition does not match its kind.");

        var layer = placement.Kind.ToString().ToLowerInvariant() + "s";
        var record = placement.Kind switch
        {
            AssetKind.Tie => CreateTieRecord(workspace, placement.ClassId),
            AssetKind.Shrub => CreateShrubRecord(placement.ClassId),
            AssetKind.Moby => CreateMobyRecord(workspace, placement.ClassId),
            _ => throw new InvalidOperationException("Unsupported UYA placement kind."),
        };
        var tieLighting = placement.Kind == AssetKind.Tie
            ? new ProjectTieLighting(0, CreateNeutralTieAmbient(
                TieClassReader.Read(canonical.ModelBytes).Header.AmbientSize))
            : null;
        return new(
            EntityId.New(),
            $"{placement.Kind.ToString().ToLowerInvariant()}:0x{placement.ClassId:X4}",
            layer,
            placement.Transform,
            new(entry.Id, entry.Kind),
            Source: new(placement.ClassId, record),
            TieLighting: tieLighting);
    }

    internal static byte[] CreateTieRecord(ForgeProjectWorkspace workspace, int classId)
    {
        var identifier = NextValue(workspace, "ties", UyaTieInstancesReader.OcclusionIdOffset);
        var bytes = new byte[UyaTieInstancesReader.RecordSize];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, classId);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), DefaultTieDrawDistance);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(UyaTieInstancesReader.OcclusionIdOffset), identifier);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x4c), 0.01f);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x54), identifier);
        return bytes;
    }

    internal static byte[] CreateNeutralTieAmbient(int size)
    {
        if (size < 0 || size % sizeof(ushort) != 0)
            throw new InvalidDataException("UYA tie ambient size must be a non-negative whole number of words.");
        var bytes = new byte[size];
        if (size >= 2) BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0x8080);
        if (size >= 4) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 0x0080);
        return bytes;
    }

    internal static byte[] CreateShrubRecord(int classId)
    {
        var bytes = new byte[UyaShrubInstancesReader.RecordSize];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, classId);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), DefaultShrubDrawDistance);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x4c), 0.01f);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x50), 255);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x54), 255);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x58), 255);
        return bytes;
    }

    private static byte[] CreateMobyRecord(ForgeProjectWorkspace workspace, int classId)
    {
        var bytes = new byte[UyaMobyInstancesReader.RecordSize];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, UyaMobyInstancesReader.RecordSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x10), NextValue(workspace, "mobys", 0x10));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x28), classId);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x2c), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x30), 1_024);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x34), 1_024);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x38), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3c), 64);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x58), -1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x68), -1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x74), 255);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x78), 255);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x7c), 255);
        return bytes;
    }

    private static int NextValue(ForgeProjectWorkspace workspace, string layer, int offset)
    {
        var used = workspace.Content.Entities
            .Where(entity => entity.Layer == layer && entity.Source?.RawRecord.Length >= offset + sizeof(int))
            .Select(entity => BinaryPrimitives.ReadInt32LittleEndian(entity.Source!.RawRecord.AsSpan(offset)))
            .Where(value => value >= 0)
            .ToHashSet();
        for (var value = 1; value < int.MaxValue; value++)
            if (!used.Contains(value)) return value;
        throw new InvalidDataException($"The UYA {layer} identifier space is exhausted.");
    }
}

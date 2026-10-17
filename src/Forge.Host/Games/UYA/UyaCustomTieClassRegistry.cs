using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal static class UyaCustomTieClassRegistry
{
    public const int FirstClassId = 0x9000;
    public const int LastClassId = ushort.MaxValue;

    public static ProjectCustomClassRegistry AllocateImported(
        ProjectCustomClassRegistry registry,
        AssetId assetId,
        IReadOnlySet<int> occupiedNativeClassIds) => Allocate(
            registry,
            assetId,
            new(ProjectCustomClassOrigin.ImportedModel),
            preferredClassId: null,
            occupiedNativeClassIds);

    public static UyaTieClassRebaseResult RebaseDonor(
        ProjectCustomClassRegistry registry,
        IReadOnlyList<UyaDonorTieClass> donorClasses,
        IReadOnlySet<int> occupiedNativeClassIds)
    {
        ArgumentNullException.ThrowIfNull(donorClasses);
        var distinct = donorClasses
            .OrderBy(value => value.AssetId.ToString(), StringComparer.Ordinal)
            .ThenBy(value => value.SourceClassId)
            .Distinct()
            .ToArray();
        if (distinct.Any(value => value.SourceClassId is < FirstClassId or > LastClassId))
            throw new InvalidDataException("Donor custom TIE class ID is outside the supported range.");
        var conflictingSource = distinct.GroupBy(value => value.SourceClassId)
            .FirstOrDefault(group => group.Select(value => value.AssetId).Distinct().Count() > 1);
        if (conflictingSource is not null)
            throw new InvalidDataException(
                $"Donor TIE class 0x{conflictingSource.Key:X4} identifies multiple assets.");

        var result = registry;
        foreach (var group in distinct.GroupBy(value => value.AssetId)
                     .OrderBy(value => value.Key.ToString(), StringComparer.Ordinal))
        {
            result = Allocate(
                result,
                group.Key,
                new(ProjectCustomClassOrigin.DonorMap, group.Min(value => value.SourceClassId)),
                group.Min(value => value.SourceClassId),
                occupiedNativeClassIds);
        }
        var targetByAsset = result.Allocations.ToDictionary(value => value.Asset.Id, value => value.ClassId);
        return new(
            result,
            distinct.Select(value => new UyaTieClassRemap(
                    value.SourceClassId,
                    value.AssetId,
                    targetByAsset[value.AssetId]))
                .OrderBy(value => value.SourceClassId)
                .ThenBy(value => value.AssetId.ToString(), StringComparer.Ordinal)
                .ToArray());
    }

    public static void Validate(ProjectCustomClassRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (registry.SchemaVersion != ProjectCustomClassSchema.CurrentVersion
            || registry.AllocationVersion != ProjectCustomClassSchema.CurrentAllocationVersion)
            throw new InvalidDataException("UYA custom TIE class registry version is unsupported.");
        if (registry.Allocations is null
            || registry.Allocations.Count > LastClassId - FirstClassId + 1
            || registry.Allocations.Any(value => value is null || value.Asset is null
                || value.Asset.Id.ToString().Length != AssetId.TextLength
                || value.Asset.Kind != AssetKind.Tie
                || value.ClassId is < FirstClassId or > LastClassId
                || value.Provenance is null
                || !Enum.IsDefined(value.Provenance.Origin)
                || (value.Provenance.Origin == ProjectCustomClassOrigin.ImportedModel
                    && value.Provenance.SourceClassId is not null)
                || (value.Provenance.Origin == ProjectCustomClassOrigin.DonorMap
                    && value.Provenance.SourceClassId is null or < FirstClassId or > LastClassId)))
            throw new InvalidDataException("UYA custom TIE class allocation is outside the supported namespace.");
        if (registry.Allocations.Select(value => value.Asset.Id).Distinct().Count() != registry.Allocations.Count
            || registry.Allocations.Select(value => value.ClassId).Distinct().Count() != registry.Allocations.Count)
            throw new InvalidDataException("UYA custom TIE class allocations must be one-to-one.");
    }

    private static ProjectCustomClassRegistry Allocate(
        ProjectCustomClassRegistry registry,
        AssetId assetId,
        ProjectCustomClassProvenance provenance,
        int? preferredClassId,
        IReadOnlySet<int> occupiedNativeClassIds)
    {
        ArgumentNullException.ThrowIfNull(occupiedNativeClassIds);
        if (assetId.ToString().Length != AssetId.TextLength)
            throw new ArgumentException("Custom TIE Asset ID is invalid.", nameof(assetId));
        Validate(registry);
        var existing = registry.Allocations.SingleOrDefault(value => value.Asset.Id == assetId);
        if (existing is not null) return registry;

        var occupied = registry.Allocations.Select(value => value.ClassId)
            .Concat(occupiedNativeClassIds)
            .Where(value => value is >= FirstClassId and <= LastClassId)
            .ToHashSet();
        var classId = preferredClassId is >= FirstClassId and <= LastClassId
            && !occupied.Contains(preferredClassId.Value)
                ? preferredClassId.Value
                : Enumerable.Range(FirstClassId, LastClassId - FirstClassId + 1)
                    .FirstOrDefault(value => !occupied.Contains(value), -1);
        if (classId < 0) throw new InvalidOperationException("UYA custom TIE class namespace is exhausted.");

        return registry with
        {
            Allocations = registry.Allocations
                .Append(new(new(assetId, AssetKind.Tie), classId, provenance))
                .OrderBy(value => value.ClassId)
                .ThenBy(value => value.Asset.Id.ToString(), StringComparer.Ordinal)
                .ToArray(),
        };
    }
}

internal readonly record struct UyaDonorTieClass(int SourceClassId, AssetId AssetId);

internal readonly record struct UyaTieClassRemap(int SourceClassId, AssetId AssetId, int TargetClassId);

internal sealed record UyaTieClassRebaseResult(
    ProjectCustomClassRegistry Registry,
    IReadOnlyList<UyaTieClassRemap> Remaps);

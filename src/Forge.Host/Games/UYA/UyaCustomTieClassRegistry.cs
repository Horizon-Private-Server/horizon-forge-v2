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
            AssetKind.Tie,
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
                AssetKind.Tie,
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

    public static UyaStaticClassRebaseResult RebaseStaticDonor(
        ProjectCustomClassRegistry registry,
        AssetKind kind,
        IReadOnlyList<UyaDonorStaticClass> donorClasses,
        IReadOnlyList<UyaDonorStaticClass> retainedTargetClasses)
    {
        if (kind is not (AssetKind.Tie or AssetKind.Shrub))
            throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentNullException.ThrowIfNull(donorClasses);
        ArgumentNullException.ThrowIfNull(retainedTargetClasses);
        Validate(registry);
        if (donorClasses.Any(value => value.SourceClassId is < 0 or > ushort.MaxValue)
            || retainedTargetClasses.Any(value => value.SourceClassId is < 0 or > ushort.MaxValue))
            throw new InvalidDataException($"{kind} class ID is outside the supported range.");
        var conflictingDonor = donorClasses.GroupBy(value => value.SourceClassId)
            .FirstOrDefault(group => group.Select(value => value.AssetId).Distinct().Count() > 1);
        if (conflictingDonor is not null)
            throw new InvalidDataException(
                $"Donor {kind} class 0x{conflictingDonor.Key:X4} identifies multiple assets.");
        var conflictingTarget = retainedTargetClasses.GroupBy(value => value.SourceClassId)
            .FirstOrDefault(group => group.Select(value => value.AssetId).Distinct().Count() > 1);
        if (conflictingTarget is not null)
            throw new InvalidDataException(
                $"Target {kind} class 0x{conflictingTarget.Key:X4} identifies multiple assets.");

        var targetByClass = retainedTargetClasses.Distinct().ToDictionary(
            value => value.SourceClassId, value => value.AssetId);
        foreach (var allocation in registry.Allocations.Where(value => value.Asset.Kind == kind))
            if (targetByClass.TryGetValue(allocation.ClassId, out var occupiedAsset)
                && occupiedAsset != allocation.Asset.Id)
                throw new InvalidDataException(
                    $"Target {kind} class 0x{allocation.ClassId:X4} conflicts with its custom allocation.");
            else targetByClass[allocation.ClassId] = allocation.Asset.Id;
        var targetClassesByAsset = retainedTargetClasses.GroupBy(value => value.AssetId)
            .ToDictionary(group => group.Key, group => group.Select(value => value.SourceClassId).Order().ToArray());
        var result = registry;
        var targetByAsset = new Dictionary<AssetId, int>();
        foreach (var group in donorClasses.Distinct().GroupBy(value => value.AssetId)
                     .OrderBy(value => value.Key.ToString(), StringComparer.Ordinal))
        {
            var allocation = result.Allocations.SingleOrDefault(value =>
                value.Asset == new ProjectAssetReference(group.Key, kind));
            if (allocation is not null)
            {
                targetByAsset[group.Key] = allocation.ClassId;
                continue;
            }
            if (targetClassesByAsset.TryGetValue(group.Key, out var exactTargetClasses))
            {
                var exactSource = group.Select(value => value.SourceClassId)
                    .Where(exactTargetClasses.Contains).DefaultIfEmpty(exactTargetClasses[0]).Min();
                targetByAsset[group.Key] = exactSource;
                continue;
            }
            var freeSourceClass = group.Select(value => value.SourceClassId).Order()
                .FirstOrDefault(value => !targetByClass.ContainsKey(value), -1);
            if (freeSourceClass >= 0)
            {
                targetByAsset[group.Key] = freeSourceClass;
                targetByClass[freeSourceClass] = group.Key;
                continue;
            }
            result = Allocate(
                result,
                kind,
                group.Key,
                new(ProjectCustomClassOrigin.DonorMap, group.Min(value => value.SourceClassId)),
                preferredClassId: null,
                targetByClass.Keys.ToHashSet());
            targetByAsset[group.Key] = result.Allocations.Single(value =>
                value.Asset == new ProjectAssetReference(group.Key, kind)).ClassId;
        }
        return new(
            result,
            donorClasses.Distinct().Select(value => new UyaStaticClassRemap(
                    value.SourceClassId, value.AssetId, targetByAsset[value.AssetId]))
                .OrderBy(value => value.SourceClassId)
                .ThenBy(value => value.AssetId.ToString(), StringComparer.Ordinal)
                .ToArray());
    }

    public static UyaStaticClassRebaseResult RebaseMobyDonor(
        ProjectCustomClassRegistry registry,
        IReadOnlyList<UyaDonorStaticClass> donorClasses,
        IReadOnlyList<UyaDonorStaticClass> retainedTargetClasses)
    {
        ArgumentNullException.ThrowIfNull(donorClasses);
        ArgumentNullException.ThrowIfNull(retainedTargetClasses);
        Validate(registry);
        if (donorClasses.Concat(retainedTargetClasses).Any(value =>
                value.SourceClassId is < 0 or > ushort.MaxValue))
            throw new InvalidDataException("Moby class ID is outside the supported range.");
        var conflictingDonor = donorClasses.GroupBy(value => value.SourceClassId)
            .FirstOrDefault(group => group.Select(value => value.AssetId).Distinct().Count() > 1);
        if (conflictingDonor is not null)
            throw new InvalidDataException(
                $"Donor Moby class 0x{conflictingDonor.Key:X4} identifies multiple assets.");
        var targetByClass = retainedTargetClasses.GroupBy(value => value.SourceClassId)
            .ToDictionary(group => group.Key, group => group.Select(value => value.AssetId).Distinct().ToArray());
        if (targetByClass.Any(value => value.Value.Length > 1))
            throw new InvalidDataException("A retained Moby class identifies multiple target assets.");
        foreach (var allocation in registry.Allocations.Where(value => value.Asset.Kind == AssetKind.Moby))
            if (targetByClass.TryGetValue(allocation.ClassId, out var allocatedClassAssets)
                && allocatedClassAssets[0] != allocation.Asset.Id)
                throw new InvalidDataException(
                    $"Target Moby class 0x{allocation.ClassId:X4} conflicts with its custom allocation.");
            else targetByClass[allocation.ClassId] = [allocation.Asset.Id];

        var result = registry;
        var remaps = new List<UyaStaticClassRemap>();
        foreach (var donor in donorClasses.Distinct().OrderBy(value => value.SourceClassId)
                     .ThenBy(value => value.AssetId.ToString(), StringComparer.Ordinal))
        {
            if (!targetByClass.TryGetValue(donor.SourceClassId, out var occupied))
            {
                targetByClass[donor.SourceClassId] = [donor.AssetId];
                remaps.Add(new(donor.SourceClassId, donor.AssetId, donor.SourceClassId));
                continue;
            }
            if (occupied[0] == donor.AssetId)
            {
                remaps.Add(new(donor.SourceClassId, donor.AssetId, donor.SourceClassId));
                continue;
            }

            var reference = new ProjectAssetReference(donor.AssetId, AssetKind.Moby);
            var allocation = result.Allocations.SingleOrDefault(value => value.Asset == reference);
            if (allocation is not null
                && allocation.Provenance.SourceClassId != donor.SourceClassId)
                throw new InvalidDataException(
                    $"Donor Moby asset {donor.AssetId} has multiple colliding source classes.");
            if (allocation is null)
                result = Allocate(
                    result,
                    AssetKind.Moby,
                    donor.AssetId,
                    new(ProjectCustomClassOrigin.DonorMap, donor.SourceClassId),
                    preferredClassId: null,
                    targetByClass.Keys.ToHashSet());
            var targetClassId = result.Allocations.Single(value => value.Asset == reference).ClassId;
            targetByClass[targetClassId] = [donor.AssetId];
            remaps.Add(new(donor.SourceClassId, donor.AssetId, targetClassId));
        }
        return new(result, remaps);
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
                || value.Asset.Kind is not (AssetKind.Tie or AssetKind.Shrub or AssetKind.Moby)
                || value.ClassId is < FirstClassId or > LastClassId
                || value.Provenance is null
                || !Enum.IsDefined(value.Provenance.Origin)
                || (value.Provenance.Origin == ProjectCustomClassOrigin.ImportedModel
                    && (value.Asset.Kind != AssetKind.Tie || value.Provenance.SourceClassId is not null))
                || (value.Provenance.Origin == ProjectCustomClassOrigin.DonorMap
                    && value.Provenance.SourceClassId is null or < 0 or > ushort.MaxValue)))
            throw new InvalidDataException("UYA custom TIE class allocation is outside the supported namespace.");
        if (registry.Allocations.Select(value => value.Asset).Distinct().Count() != registry.Allocations.Count
            || registry.Allocations.Select(value => (value.Asset.Kind, value.ClassId)).Distinct().Count()
                != registry.Allocations.Count)
            throw new InvalidDataException("UYA custom TIE class allocations must be one-to-one.");
    }

    private static ProjectCustomClassRegistry Allocate(
        ProjectCustomClassRegistry registry,
        AssetKind kind,
        AssetId assetId,
        ProjectCustomClassProvenance provenance,
        int? preferredClassId,
        IReadOnlySet<int> occupiedNativeClassIds)
    {
        ArgumentNullException.ThrowIfNull(occupiedNativeClassIds);
        if (assetId.ToString().Length != AssetId.TextLength)
            throw new ArgumentException("Custom TIE Asset ID is invalid.", nameof(assetId));
        Validate(registry);
        var reference = new ProjectAssetReference(assetId, kind);
        var existing = registry.Allocations.SingleOrDefault(value => value.Asset == reference);
        if (existing is not null) return registry;

        var occupied = registry.Allocations.Where(value => value.Asset.Kind == kind).Select(value => value.ClassId)
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
                .Append(new(reference, classId, provenance))
                .OrderBy(value => value.Asset.Kind)
                .ThenBy(value => value.ClassId)
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

internal readonly record struct UyaDonorStaticClass(int SourceClassId, AssetId AssetId);

internal readonly record struct UyaStaticClassRemap(int SourceClassId, AssetId AssetId, int TargetClassId);

internal sealed record UyaStaticClassRebaseResult(
    ProjectCustomClassRegistry Registry,
    IReadOnlyList<UyaStaticClassRemap> Remaps);

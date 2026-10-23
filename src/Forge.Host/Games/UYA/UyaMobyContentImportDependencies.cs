using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Sounds;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static partial class UyaMobyContentImportService
{
    internal static IReadOnlyList<string> CapabilityBlockers(UyaBaseLevelData source) =>
        CapabilityBlockers(source.Entities, source.MobyPVars, source.OpaqueSections);

    internal static IReadOnlyList<string> CapabilityBlockers(
        IReadOnlyList<ProjectEntity> entities,
        ProjectMobyPVarState? pvars,
        IReadOnlyList<OpaqueSectionCapture>? opaqueSections = null)
    {
        var result = new List<string>();
        var mobys = entities.Where(value => value.Layer == "mobys").ToArray();
        var nativeRecordsValid = true;
        foreach (var entity in mobys)
        {
            if (entity.Source?.RawRecord.Length != UyaMobyInstancesReader.RecordSize
                || entity.Provenance is null || entity.Asset is null && entity.Source?.ModelLess != true)
            {
                nativeRecordsValid = false;
                result.Add($"Donor Moby {entity.Name} is missing its native record or model dependency.");
                continue;
            }
        }
        if (nativeRecordsValid
            && mobys.Select(value => value.Provenance!.SourceIndex).Distinct().Count() != mobys.Length)
        {
            nativeRecordsValid = false;
            result.Add("Donor Mobys have duplicate native source indexes.");
        }
        try
        {
            if (nativeRecordsValid) ReadDonorGroups(mobys, opaqueSections ?? []);
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException)
        {
            result.Add($"Donor Moby groups are malformed ({exception.Message}).");
        }
        foreach (var pvar in pvars?.Entries ?? [])
        {
            var knownMobyLinks = pvar.References.Where(value =>
                    value.Reference.EntityKind == ProjectEntityKind.Moby)
                .Select(value => value.Offset).ToHashSet();
            if (pvar.MobyLinkOffsets.Any(value => !knownMobyLinks.Contains(value)))
                result.Add($"Donor Moby PVar {pvar.EntityId} contains an opaque Moby link.");
        }
        return result.Distinct(StringComparer.Ordinal)
            .Take(ContentImportPlanSchema.MaximumDiagnostics).ToArray();
    }

    internal static IReadOnlyList<UyaMobyExternalReferenceRequirement> ExternalReferenceRequirements(
        UyaBaseLevelData source) => ExternalReferenceRequirements(source.Entities, source.MobyPVars);

    internal static IReadOnlyList<UyaMobyExternalReferenceRequirement> ExternalReferenceRequirements(
        IReadOnlyList<ProjectEntity> entities,
        ProjectMobyPVarState? pvars)
    {
        var mobys = entities.Where(value => value.Layer == "mobys" && value.Provenance is not null)
            .ToDictionary(value => value.EntityId);
        return (pvars?.Entries ?? []).Where(value => mobys.ContainsKey(value.EntityId))
            .SelectMany(pvar => pvar.References.Where(reference =>
                    reference.SourceValue != reference.NullValue
                    && (reference.Reference.EntityKind != ProjectEntityKind.Moby
                        || reference.Reference.EntityId is null
                        || !mobys.ContainsKey(reference.Reference.EntityId.Value)))
                .Select(reference => new UyaMobyExternalReferenceRequirement(
                    mobys[pvar.EntityId].Provenance!.SourceIndex,
                    reference.Reference.FieldKey,
                    reference.SourceValue,
                    reference.Reference.EntityKind!.Value,
                    reference.Reference.Nullable)))
            .OrderBy(value => value.OwnerSourceIndex)
            .ThenBy(value => value.FieldKey, StringComparer.Ordinal)
            .ThenBy(value => value.SourceValue)
            .ToArray();
    }

    private static MobyGroupImport ImportGroups(
        UyaMobyContentImportSource source,
        ContentImportPlan plan,
        IReadOnlyList<ProjectEntity> donor,
        IReadOnlyList<ProjectEntity> retained,
        IReadOnlyDictionary<EntityId, EntityId> targetEntityIds)
    {
        var groups = ReadDonorGroups(donor, source.OpaqueSections);
        var used = donor.Select(value => UyaMobyInstancesReader.ReadInstance(value.Source!.RawRecord).Group)
            .Where(value => value >= 0).Distinct().Order().ToArray();
        if (used.Length == 0) return new(new Dictionary<int, int>(), []);

        var occupied = retained.Where(value => value.Layer == "mobys" && value.Source is not null)
            .Select(value => UyaMobyInstancesReader.ReadInstance(value.Source!.RawRecord).Group)
            .Where(value => value >= 0).ToHashSet();
        var targetIds = new Dictionary<int, int>();
        foreach (var sourceId in used)
        {
            var targetId = sourceId;
            if (!occupied.Add(targetId))
            {
                targetId = Enumerable.Range(0, ProjectGroupSchema.MaximumGroupCount)
                    .FirstOrDefault(value => !occupied.Contains(value), -1);
                if (targetId < 0) throw new InvalidDataException("The UYA Moby group ID space is exhausted.");
                occupied.Add(targetId);
            }
            targetIds.Add(sourceId, targetId);
        }

        var bySourceIndex = donor.ToDictionary(value => value.Provenance!.SourceIndex);
        var imported = used.Select(sourceId => new ProjectGroup(
            new(StableGuid($"{plan.BatchId}\ngroup\nmobys\n{sourceId}")),
            $"Imported level {plan.SourceLevel} mobys group {sourceId + 1}",
            groups[sourceId].Select(sourceIndex =>
                targetEntityIds[bySourceIndex[sourceIndex].EntityId]).ToArray())).ToArray();
        return new(targetIds, imported);
    }

    private static IReadOnlyList<int[]> ReadDonorGroups(
        IReadOnlyList<ProjectEntity> donor,
        IReadOnlyList<OpaqueSectionCapture> opaqueSections)
    {
        var nativeGroups = donor.ToDictionary(
            value => value.Provenance!.SourceIndex,
            value => UyaMobyInstancesReader.ReadInstance(value.Source!.RawRecord).Group);
        if (nativeGroups.Values.Any(value => value < -1))
            throw new InvalidDataException("A native Moby group ID is below -1.");
        var grouped = nativeGroups.Values.Any(value => value >= 0);
        var sections = opaqueSections.Where(value => value.Name == "gameplay/moby_groups").ToArray();
        if (sections.Length == 0)
        {
            if (grouped) throw new InvalidDataException("The native Moby group section is missing.");
            return [];
        }
        if (sections.Length > 1) throw new InvalidDataException("Duplicate native Moby group sections were found.");
        var groups = UyaTieGroupsReader.Read(sections[0].Bytes).Groups;
        if (groups.Count > ProjectGroupSchema.MaximumGroupCount)
            throw new InvalidDataException("The native Moby group count exceeds the supported limit.");
        var seen = new HashSet<int>();
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            foreach (var sourceIndex in groups[groupIndex])
            {
                if (!nativeGroups.TryGetValue(sourceIndex, out var recordGroup))
                    throw new InvalidDataException(
                        $"Native Moby group {groupIndex} references missing instance {sourceIndex}.");
                if (!seen.Add(sourceIndex))
                    throw new InvalidDataException($"Native Moby instance {sourceIndex} occurs in multiple groups.");
                if (recordGroup != groupIndex)
                    throw new InvalidDataException(
                        $"Native Moby instance {sourceIndex} names group {recordGroup}, not {groupIndex}.");
            }
        }
        foreach (var (sourceIndex, group) in nativeGroups.Where(value => value.Value >= 0))
            if (group >= groups.Count || !groups[group].Contains(sourceIndex))
                throw new InvalidDataException(
                    $"Native Moby instance {sourceIndex} is missing from group {group}.");
        return groups;
    }

    private static ProjectMobyPVarState? ImportPVars(
        ProjectMobyPVarState? target,
        ProjectMobyPVarState? donor,
        IReadOnlyDictionary<EntityId, ProjectEntity> donorEntities,
        IReadOnlyDictionary<EntityId, EntityId> targetIds,
        IReadOnlyList<ProjectEntity> retainedEntities,
        IReadOnlySet<EntityId> removed,
        IReadOnlyList<UyaMobyExternalReferenceChoice> referenceChoices)
    {
        var retained = (target?.Entries ?? []).Where(value => !removed.Contains(value.EntityId)).ToArray();
        foreach (var pvar in retained)
            if (pvar.References.Any(value => value.Reference.EntityId is { } id && removed.Contains(id)))
                throw new InvalidOperationException(
                    $"Retained Moby PVar {pvar.EntityId} still references a removed target.");
        var donorPvars = (donor?.Entries ?? []).ToDictionary(value => value.EntityId);
        if (referenceChoices.Count > ContentImportPlanSchema.MaximumPlanItems)
            throw new InvalidDataException("The Moby external-reference choice list exceeds the import limit.");
        if (referenceChoices.Select(value => (value.OwnerSourceIndex, value.FieldKey, value.SourceValue))
                .Distinct().Count() != referenceChoices.Count)
            throw new InvalidDataException("Moby external-reference choices must be unique.");
        var choices = referenceChoices.ToDictionary(
            value => (value.OwnerSourceIndex, value.FieldKey, value.SourceValue));
        var usedChoices = new HashSet<(int OwnerSourceIndex, string FieldKey, int SourceValue)>();
        var retainedById = retainedEntities.ToDictionary(value => value.EntityId);
        var imported = new List<ProjectMobyPVar>();
        foreach (var (sourceId, targetId) in targetIds)
        {
            var sourceEntity = donorEntities[sourceId];
            var native = UyaMobyInstancesReader.ReadInstance(sourceEntity.Source!.RawRecord);
            if (!donorPvars.TryGetValue(sourceId, out var pvar))
            {
                if (native.PvarIndex >= 0)
                    throw new InvalidDataException($"Donor Moby {sourceEntity.Name} is missing PVar {native.PvarIndex}.");
                continue;
            }
            var knownMobyLinks = pvar.References.Where(value =>
                    value.Reference.EntityKind == ProjectEntityKind.Moby)
                .Select(value => value.Offset).ToHashSet();
            var opaque = pvar.MobyLinkOffsets.FirstOrDefault(value => !knownMobyLinks.Contains(value), -1);
            if (opaque >= 0)
                throw new InvalidDataException(
                    $"Donor Moby {sourceEntity.Name} has an opaque PVar Moby link at 0x{opaque:X}.");
            var references = pvar.References.Select(value =>
            {
                if (value.SourceValue == value.NullValue)
                    return value with { Reference = value.Reference with { EntityId = null } };
                if (value.Reference.EntityKind == ProjectEntityKind.Moby
                    && value.Reference.EntityId is { } sourceTarget
                    && targetIds.TryGetValue(sourceTarget, out var target))
                    return value with { Reference = value.Reference with { EntityId = target } };

                var key = (sourceEntity.Provenance!.SourceIndex,
                    value.Reference.FieldKey, value.SourceValue);
                if (!choices.TryGetValue(key, out var choice))
                    throw new InvalidDataException(
                        $"Donor Moby {sourceEntity.Name} PVar field {value.Reference.FieldKey} "
                        + "requires an explicit map or clear choice.");
                usedChoices.Add(key);
                if (!Enum.IsDefined(choice.Action))
                    throw new InvalidDataException("A Moby external-reference choice has an invalid action.");
                if (choice.Action == UyaMobyExternalReferenceAction.Clear)
                {
                    if (!value.Reference.Nullable || choice.TargetEntityId is not null)
                        throw new InvalidDataException(
                            $"Donor Moby {sourceEntity.Name} PVar field {value.Reference.FieldKey} cannot be cleared.");
                    return value with { Reference = value.Reference with { EntityId = null } };
                }
                if (choice.TargetEntityId is not { } targetId
                    || !retainedById.TryGetValue(targetId, out var targetEntity)
                    || ProjectReferences.KindOf(targetEntity) != value.Reference.EntityKind)
                    throw new InvalidDataException(
                        $"Donor Moby {sourceEntity.Name} PVar field {value.Reference.FieldKey} "
                        + "maps to a missing or incompatible target entity.");
                return value with { Reference = value.Reference with { EntityId = targetId } };
            }).ToArray();
            imported.Add(pvar with
            {
                EntityId = targetId,
                SourceTableIndex = null,
                Data = pvar.Data.ToArray(),
                BaselineData = pvar.BaselineData.ToArray(),
                MobyLinkOffsets = pvar.MobyLinkOffsets.ToArray(),
                RelativePointerOffsets = pvar.RelativePointerOffsets.ToArray(),
                References = references,
            });
        }
        if (usedChoices.Count != choices.Count)
            throw new InvalidDataException("A Moby external-reference choice does not match the donor PVar graph.");
        if (target is null && imported.Count == 0) return null;
        return new(
            ProjectMobyPVarSchema.CurrentVersion,
            target?.SourceTableEntryCount ?? 0,
            retained.Concat(imported).ToArray());
    }

    private static ProjectMobyDexState? ImportMobyDex(
        ProjectMobyDexState? target,
        IReadOnlyList<UyaStaticClassRemap> remaps,
        MobyDexCatalog catalog)
    {
        var entries = (target?.Entries ?? []).ToList();
        foreach (var remap in remaps.Where(value => value.SourceClassId != value.TargetClassId))
        {
            var source = catalog.ResolveValidated(null, "UYA", remap.SourceClassId)?.Entry;
            if (source is null) continue;
            var imported = source with
            {
                OClass = remap.TargetClassId,
                Name = $"{source.Name} (imported 0x{remap.SourceClassId:X4})",
            };
            var existing = entries.SingleOrDefault(value =>
                value.Game == imported.Game && value.OClass == imported.OClass);
            if (existing is not null && existing != imported)
                throw new InvalidDataException(
                    $"Target MobyDex class 0x{imported.OClass:X4} conflicts with the donor overlay.");
            if (existing is null) entries.Add(imported);
        }
        if (entries.Count == 0) return null;
        var state = new ProjectMobyDexState(
            ProjectMobyDexSchema.CurrentVersion,
            entries.OrderBy(value => value.Game, StringComparer.Ordinal).ThenBy(value => value.OClass).ToArray());
        MobyDexCatalog.ValidateProjectState(state);
        return state;
    }

    private static async Task<ProjectSoundState> ImportSoundsAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        UyaMobyContentImportSource source,
        ContentImportPlan plan,
        IReadOnlyList<ProjectEntity> imported,
        IReadOnlyList<ProjectEntity> donor,
        IReadOnlyList<UyaStaticClassRemap> remaps,
        IReadOnlyList<ProjectEntity> retained,
        IDictionary<AssetId, ProjectAssetAttachment> attachments,
        CancellationToken cancellationToken)
    {
        var retainedClasses = retained.Where(value => value.Layer == "mobys")
            .Select(value => value.Source!.ClassId).ToHashSet();
        var state = workspace.Content.Sounds with
        {
            MobyRemaps = workspace.Content.Sounds.MobyRemaps
                .Where(value => retainedClasses.Contains(value.ClassId)).ToArray(),
        };
        var targetBySource = remaps.ToDictionary(
            value => (value.SourceClassId, value.AssetId), value => value.TargetClassId);
        var banks = new Dictionary<AssetId, (byte[] Bytes, SoundBankInventory Inventory)>();
        foreach (var pair in donor.Zip(imported).Where(value => value.First.Asset is not null)
                     .DistinctBy(value => (value.First.Source!.ClassId, value.First.Asset!.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var donorEntity = pair.First;
            var importedEntity = pair.Second;
            var entry = catalog.Query(new(Id: donorEntity.Asset!.Id)).SingleOrDefault()
                ?? throw new FileNotFoundException($"Donor Moby asset {donorEntity.Asset.Id} is missing.");
            var catalogSource = entry.Sources.SingleOrDefault(value =>
                value.Game == "UYA" && value.Region == "NTSC-U" && value.Revision == "1.00"
                && value.Level == $"level{plan.SourceLevel:00}"
                && value.Fingerprint.Equals(plan.SourceFingerprint, StringComparison.OrdinalIgnoreCase)
                && value.MobySounds is { } sounds
                && (sounds.ClassId is null || sounds.ClassId == donorEntity.Source!.ClassId));
            if (catalogSource?.MobySounds is not { Slots.Count: > 0 } use) continue;
            var targetClass = targetBySource[(donorEntity.Source!.ClassId, donorEntity.Asset.Id)];
            var existing = state.MobyRemaps.SingleOrDefault(value => value.ClassId == targetClass);
            if (existing is not null
                && retained.Any(value => value.Layer == "mobys" && value.Source?.ClassId == targetClass)
                && !Equivalent(existing, use))
                throw new InvalidDataException(
                    $"Moby 0x{targetClass:X4} has a different retained target sound mapping.");
            if (!banks.TryGetValue(use.Bank, out var bank))
            {
                var bytes = await UyaInstancedCollisionCompositionService.ReadAssetAsync(
                    workspace, catalog, use.Bank, AssetKind.SoundBank, cancellationToken);
                bank = (bytes, SoundBankReader.Inspect(GameId.UYA, bytes, cancellationToken));
                banks.Add(use.Bank, bank);
            }
            var soundPlan = UyaMobySoundImportService.Prepare(
                state,
                source.Sounds,
                bank.Bytes,
                bank.Inventory,
                use,
                targetClass,
                importedEntity.Asset!);
            state = soundPlan.State;
            foreach (var attachment in soundPlan.Attachments)
            {
                var id = AssetId.Compute(
                    attachment.Kind, attachment.CanonicalFormatVersion, attachment.CanonicalBytes.Span);
                if (attachments.TryGetValue(id, out var current)
                    && (current.Kind != attachment.Kind
                        || current.CanonicalFormatVersion != attachment.CanonicalFormatVersion
                        || !current.CanonicalBytes.Span.SequenceEqual(attachment.CanonicalBytes.Span)))
                    throw new InvalidDataException($"Imported sound asset {id} has inconsistent data.");
                attachments[id] = attachment;
            }
        }
        return state;
    }

    private static bool Equivalent(ProjectMobySoundMap target, AssetMobySoundUse donor) =>
        target.Slots.OrderBy(value => value.Slot).Select(value => (value.Slot, value.Sound?.Id))
            .SequenceEqual(donor.Slots.OrderBy(value => value.Slot).Select(value => (value.Slot, value.Sound)));

}

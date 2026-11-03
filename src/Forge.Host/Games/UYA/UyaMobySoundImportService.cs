using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Sounds;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal sealed record UyaMobySoundImportPlan(
    ProjectSoundState State,
    IReadOnlyList<ProjectAssetAttachment> Attachments);

internal static class UyaMobySoundImportService
{
    internal static ProjectSoundState NormalizeLegacyRemaps(ProjectSoundState state)
    {
        if (state.SourceRemapRange is not { } source) return state;
        var changed = false;
        var maps = state.MobyRemaps.Select(map =>
        {
            if (map.RawListOffset <= 0 || map.Slots.Count == 0) return map;
            var first = map.Slots[0];
            if (first.NativeProgramIndex != -1 || first.Sound is not null
                || first.SourceRange.Length != 4 || first.SourceRange.Offset != source.Offset + map.RawListOffset)
                return map;
            // Older inventories exposed the primary bank selector as a null sound slot.
            changed = true;
            return map with { Slots = map.Slots.Skip(1).Select((slot, index) => slot with { Slot = index }).ToArray() };
        }).ToArray();
        return changed ? state with { MobyRemaps = maps } : state;
    }

    public static async Task<EditorDiagnostic?> HydrateLegacyRemapsAsync(
        ForgeProjectWorkspace workspace, CancellationToken cancellationToken)
    {
        var state = NormalizeLegacyRemaps(workspace.Content.Sounds);
        if (ReferenceEquals(state, workspace.Content.Sounds)) return null;
        await workspace.SetSoundsAsync(state, cancellationToken: cancellationToken);
        return new("uya-sounds.remaps-repaired", EditorDiagnosticSeverity.Info,
            "Corrected legacy UYA sound-slot numbering. Save the project and rebuild occlusion and the map.");
    }

    public static async Task ImportAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        AssetCatalogEntry moby,
        int classId,
        CancellationToken cancellationToken,
        AssetSourceSelection? selection = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = SelectSource(workspace, moby, classId, selection);
        var reference = new ProjectAssetReference(moby.Id, AssetKind.Moby);
        var existing = workspace.Content.Sounds.MobyRemaps.SingleOrDefault(value => value.ClassId == classId);
        var slots = source?.MobySounds?.Slots ?? [];
        if (source is not null && existing?.Moby == reference && existing.Source == AssetSourceSelection.From(source)
            && slots.All(slot => slot.NativeProgramIndex < 0 || slot.Sound is not null)
            && existing.Slots.OrderBy(slot => slot.Slot).Select(slot => (slot.Slot, slot.NativeProgramIndex, slot.Sound?.Id))
                .SequenceEqual(slots.OrderBy(slot => slot.Slot).Select(slot => (slot.Slot, slot.NativeProgramIndex, slot.Sound))))
            return;
        if (source?.MobySounds is not { Slots.Count: > 0 } use)
        {
            if (selection is null) return;
            EnsureSourceCanChange(workspace, existing, [], reference);
            var map = new ProjectMobySoundMap(existing?.DefinitionIndex
                ?? workspace.Content.Sounds.MobyRemaps.Select(value => value.DefinitionIndex).DefaultIfEmpty(-1).Max() + 1,
                classId, reference, 0, new(0, 0), [], AssetSourceSelection.From(source!));
            await workspace.SetSoundsAsync(workspace.Content.Sounds with
            {
                MobyRemaps = workspace.Content.Sounds.MobyRemaps.Where(value => value.ClassId != classId).Append(map).ToArray(),
            }, cancellationToken: cancellationToken);
            return;
        }
        EnsureSourceCanChange(workspace, existing, use.Slots, reference);
        var bankBytes = await UyaInstancedCollisionCompositionService.ReadAssetAsync(
            workspace, catalog, use.Bank, AssetKind.SoundBank, cancellationToken);
        var sourceGame = Enum.Parse<GameId>(source.Game);
        var bank = SoundBankReader.Inspect(sourceGame, bankBytes, cancellationToken);
        var donor = SoundProjectService.CreateBank(
            sourceGame, ParseLevel(source.Level), bankBytes, bank);
        var plan = Prepare(
            workspace.Content.Sounds,
            donor,
            bankBytes,
            bank,
            use,
            classId,
            reference,
            AssetSourceSelection.From(source));
        await workspace.SetSoundsAsync(plan.State, plan.Attachments, cancellationToken);
    }

    public static async Task<EditorDiagnostic?> HydrateConvertedAsync(ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog, CancellationToken cancellationToken)
    {
        var imported = 0;
        var warnings = new List<string>();
        foreach (var allocation in workspace.Content.CustomClasses.Allocations.Where(value =>
            value.Asset.Kind == AssetKind.Moby && value.Provenance.Origin == ProjectCustomClassOrigin.ConvertedAsset))
        {
            if (workspace.Content.Sounds.MobyRemaps.Any(value => value.ClassId == allocation.ClassId)) continue;
            var entry = catalog.Query(new(Id: allocation.Asset.Id)).SingleOrDefault();
            if (entry is null || !entry.Sources.Any(value => value.Game == "DL" && value.MobySounds?.Slots.Count > 0)) continue;
            try
            {
                await ImportAsync(workspace, catalog, entry, allocation.ClassId, cancellationToken);
                if (workspace.Content.Sounds.MobyRemaps.Any(value => value.ClassId == allocation.ClassId)) imported++;
            }
            catch (Exception error) when (error is InvalidDataException or FileNotFoundException)
            { warnings.Add($"DL moby 0x{allocation.ClassId:X4} sounds could not be imported: {error.Message}"); }
        }
        return imported == 0 && warnings.Count == 0 ? null : new("dl-moby.sounds-hydrated",
            warnings.Count == 0 ? EditorDiagnosticSeverity.Info : EditorDiagnosticSeverity.Warning,
            $"Imported sounds for {imported} converted DL moby class(es). " + string.Join(' ', warnings));
    }

    internal static UyaMobySoundImportPlan Prepare(
        ProjectSoundState target,
        ProjectSoundState donor,
        byte[] donorBankBytes,
        SoundBankInventory donorBank,
        AssetMobySoundUse use,
        int classId,
        ProjectAssetReference moby,
        AssetSourceSelection? selection = null)
    {
        if (target.Programs.Count == 0)
            throw new InvalidDataException("The target project sound inventory is missing.");
        var donorPrograms = donor.Programs.Where(value => value.Asset is not null)
            .GroupBy(value => value.Asset!.Id).ToDictionary(value => value.Key, value => value.First());
        var donorProgramsByNativeIndex = donor.Programs.ToDictionary(value => value.Provenance.NativeIndex);
        var targetProgramIds = target.Programs.Where(value => value.Asset is not null)
            .Select(value => value.Asset!.Id).ToHashSet();
        var requiredPrograms = new HashSet<AssetId>();
        foreach (var slot in use.Slots.Where(value => value.NativeProgramIndex >= 0))
        {
            if (slot.Sound is not { } sound)
                throw new InvalidDataException(
                    $"Moby 0x{classId:X4} sound slot {slot.Slot} is not importable: "
                    + (slot.PreviewBlocker ?? "its program identity is unavailable."));
            if (!donorProgramsByNativeIndex.TryGetValue(slot.NativeProgramIndex, out var nativeProgram)
                || nativeProgram.Asset?.Id != sound)
                throw new InvalidDataException(
                    $"Moby 0x{classId:X4} sound slot {slot.Slot} does not match its catalog bank.");
            Visit(sound);
        }

        var appendedPrograms = donor.Programs.Where(value =>
                value.Asset is { } asset && requiredPrograms.Contains(asset.Id)
                && !targetProgramIds.Contains(asset.Id))
            .Select(value => value with
            {
                Provenance = value.Provenance with { Origin = ProjectSoundOrigin.Imported },
            }).ToArray();
        var requiredSampleIds = appendedPrograms.SelectMany(value => value.Operations)
            .Where(value => value.Tone is not null)
            .Select(value => value.Tone!.Sample.Id).ToHashSet();
        var targetSampleIds = target.Samples.Select(value => value.Asset.Id).ToHashSet();
        var donorSamples = donor.Samples.ToDictionary(value => value.Asset.Id);
        var sourceSamples = donorBank.Samples.GroupBy(value => value.ContentId, StringComparer.Ordinal)
            .ToDictionary(value => value.Key, value => value.First(), StringComparer.Ordinal);
        var appendedSamples = requiredSampleIds.Where(value => !targetSampleIds.Contains(value)).Select(id =>
        {
            if (!donorSamples.TryGetValue(id, out var sample)
                || !sourceSamples.TryGetValue(sample.SourceContentId, out var source))
                throw new InvalidDataException($"Imported sound graph is missing sample {id}.");
            var bytes = donorBankBytes.AsSpan(source.Provenance.Offset, source.Provenance.Length).ToArray();
            if (ProjectSounds.SampleId(bytes) != id)
                throw new InvalidDataException($"Imported sound sample {id} failed identity verification.");
            return (Sample: sample with
            {
                Provenance = sample.Provenance.Select(value =>
                    value with { Origin = ProjectSoundOrigin.Imported }).ToArray(),
            }, Attachment: new ProjectAssetAttachment(
                AssetKind.SoundSample, ProjectSoundSchema.SampleCanonicalFormatVersion, bytes));
        }).OrderBy(value => value.Sample.Asset.Id.ToString(), StringComparer.Ordinal).ToArray();

        var existingMap = target.MobyRemaps.SingleOrDefault(value => value.ClassId == classId);
        var slotsByIndex = use.Slots.ToDictionary(value => value.Slot);
        var slotCount = slotsByIndex.Count == 0 ? 0 : slotsByIndex.Keys.Max() + 1;
        var slots = Enumerable.Range(0, slotCount).Select(index =>
        {
            if (!slotsByIndex.TryGetValue(index, out var slot))
                return new ProjectSoundRemapSlot(index, -1, null, new(0, 0), null);
            return slot.NativeProgramIndex < 0
                ? new(index, -1, null, new(0, 0), null)
                : new ProjectSoundRemapSlot(
                    index,
                    slot.NativeProgramIndex,
                    new(slot.Sound!.Value, AssetKind.Sound),
                    new(0, 0),
                    null);
        }).ToArray();
        var map = new ProjectMobySoundMap(
            existingMap?.DefinitionIndex
                ?? target.MobyRemaps.Select(value => value.DefinitionIndex).DefaultIfEmpty(-1).Max() + 1,
            classId,
            moby,
            checked((short)(slots.Length == 0 ? 0 : 1)),
            new(0, 0),
            slots,
            selection);
        var maps = existingMap is null
            ? target.MobyRemaps.Append(map).ToArray()
            : target.MobyRemaps.Select(value => value.ClassId == classId ? map : value).ToArray();
        return new(
            target with
            {
                Programs = target.Programs.Concat(appendedPrograms).ToArray(),
                Samples = target.Samples.Concat(appendedSamples.Select(value => value.Sample)).ToArray(),
                MobyRemaps = maps,
            },
            appendedSamples.Select(value => value.Attachment).ToArray());

        void Visit(AssetId id)
        {
            if (targetProgramIds.Contains(id) || !requiredPrograms.Add(id)) return;
            if (!donorPrograms.TryGetValue(id, out var program))
                throw new InvalidDataException($"Imported sound graph is missing program {id}.");
            foreach (var dependency in program.Operations
                .Where(value => value.ReferencedProgram is not null)
                .Select(value => value.ReferencedProgram!.Id)) Visit(dependency);
        }
    }

    private static AssetSource? SelectSource(
        ForgeProjectWorkspace workspace,
        AssetCatalogEntry moby,
        int classId,
        AssetSourceSelection? selection)
    {
        var allocation = workspace.Content.CustomClasses.Allocations.SingleOrDefault(value => value.Asset.Id == moby.Id
            && value.Provenance.Origin == ProjectCustomClassOrigin.ConvertedAsset);
        var sourceGame = allocation is null ? workspace.Manifest.Target.Game : "DL";
        var sourceClass = allocation?.Provenance.SourceClassId ?? classId;
        selection ??= workspace.Content.Sounds.MobyRemaps.SingleOrDefault(value => value.ClassId == classId
            && value.Moby?.Id == moby.Id)?.Source;
        if (selection is not null)
        {
            selection.Validate();
            return moby.Sources.SingleOrDefault(value => selection.Matches(value)
                && value.Game == sourceGame && value.Region == workspace.Manifest.Target.Region
                && value.Revision == workspace.Manifest.Target.Revision
                && (value.MobySounds?.ClassId is null || value.MobySounds.ClassId == sourceClass))
                ?? throw new InvalidDataException("The selected moby sound source is unavailable; refresh the asset selection.");
        }
        var candidates = moby.Sources.Where(value =>
            value.Game == sourceGame
            && value.Region == workspace.Manifest.Target.Region
            && value.Revision == workspace.Manifest.Target.Revision
            && value.MobySounds is { } sounds
            && (sounds.ClassId is null || sounds.ClassId == sourceClass)).ToArray();
        if (candidates.Length == 0) return null;
        if (allocation is not null)
            // DL level numbers do not identify UYA donor levels; use the first complete source variant.
            return candidates.OrderBy(value => value.MobySounds!.Slots.Count == 0 ? 2
                    : value.MobySounds.Slots.Any(slot => slot.Sound is null) ? 1 : 0)
                .ThenBy(value => value.Level, StringComparer.Ordinal).ThenBy(value => value.Archive, StringComparer.Ordinal).First();
        var targetLevel = $"level{workspace.Manifest.BaseLevel.Level:00}";
        var preferred = candidates.Where(value => value.Level == targetLevel).ToArray();
        if (preferred.Length > 0) candidates = preferred;
        var first = candidates[0];
        if (candidates.Skip(1).Any(value => !Equivalent(first.MobySounds!, value.MobySounds!)))
            throw new InvalidDataException(
                $"Moby asset {moby.Id} has different sound mappings in multiple source levels; "
                + "select an exact source variant before placement.");
        return first;
    }

    private static void EnsureSourceCanChange(ForgeProjectWorkspace workspace, ProjectMobySoundMap? existing,
        IReadOnlyList<AssetSoundSlot> slots, ProjectAssetReference moby)
    {
        if (existing is null || !workspace.Content.Entities.Any(entity => entity.Layer == "mobys"
            && entity.Source?.ClassId == existing.ClassId)) return;
        var before = existing.Slots.OrderBy(value => value.Slot).Select(value => (value.Slot, value.Sound?.Id));
        var after = slots.OrderBy(value => value.Slot).Select(value => (value.Slot, value.Sound));
        if (existing.Moby is not null && existing.Moby != moby || !before.SequenceEqual(after))
            throw new InvalidDataException($"Moby class 0x{existing.ClassId:X4} already uses another sound variant in the scene. "
                + "Delete its instances before importing this source; all instances of an oClass share its sounds.");
    }

    private static bool Equivalent(AssetMobySoundUse left, AssetMobySoundUse right) =>
        left.Slots.OrderBy(value => value.Slot).SequenceEqual(right.Slots.OrderBy(value => value.Slot));

    private static int ParseLevel(string value) =>
        value.StartsWith("level", StringComparison.Ordinal)
        && int.TryParse(value.AsSpan(5), out var level)
        && level is >= 0 and <= 999
            ? level
            : throw new InvalidDataException($"Sound source level '{value}' is invalid.");
}

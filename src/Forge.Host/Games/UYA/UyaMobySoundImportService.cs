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
    public static async Task ImportAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        AssetCatalogEntry moby,
        int classId,
        CancellationToken cancellationToken)
    {
        var source = SelectSource(workspace, moby, classId);
        if (source?.MobySounds is not { } use || use.Slots.Count == 0) return;
        var bankBytes = await UyaInstancedCollisionCompositionService.ReadAssetAsync(
            workspace, catalog, use.Bank, AssetKind.SoundBank, cancellationToken);
        var bank = SoundBankReader.Inspect(GameId.UYA, bankBytes, cancellationToken);
        var donor = SoundProjectService.CreateBank(
            GameId.UYA, ParseLevel(source.Level), bankBytes, bank);
        var plan = Prepare(
            workspace.Content.Sounds,
            donor,
            bankBytes,
            bank,
            use,
            classId,
            new(moby.Id, AssetKind.Moby));
        await workspace.SetSoundsAsync(plan.State, plan.Attachments, cancellationToken);
    }

    internal static UyaMobySoundImportPlan Prepare(
        ProjectSoundState target,
        ProjectSoundState donor,
        byte[] donorBankBytes,
        SoundBankInventory donorBank,
        AssetMobySoundUse use,
        int classId,
        ProjectAssetReference moby)
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
            slots);
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
        int classId)
    {
        var candidates = moby.Sources.Where(value =>
            value.Game == workspace.Manifest.Target.Game
            && value.Region == workspace.Manifest.Target.Region
            && value.Revision == workspace.Manifest.Target.Revision
            && value.MobySounds is { } sounds
            && (sounds.ClassId is null || sounds.ClassId == classId)).ToArray();
        if (candidates.Length == 0) return null;
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

    private static bool Equivalent(AssetMobySoundUse left, AssetMobySoundUse right) =>
        left.Slots.OrderBy(value => value.Slot).SequenceEqual(right.Slots.OrderBy(value => value.Slot));

    private static int ParseLevel(string value) =>
        value.StartsWith("level", StringComparison.Ordinal)
        && int.TryParse(value.AsSpan(5), out var level)
        && level is >= 0 and <= 999
            ? level
            : throw new InvalidDataException($"Sound source level '{value}' is invalid.");
}

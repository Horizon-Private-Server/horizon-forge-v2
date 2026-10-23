using System.Security.Cryptography;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Sounds;

namespace Forge.Host.Domain;

internal static class SoundProjectService
{
    public static ProjectSoundState CreateBank(
        GameId sourceGame,
        int level,
        byte[] bankBytes,
        SoundBankInventory bank) => Create(
        sourceGame,
        level,
        bankBytes,
        bank,
        new(new(0, 0), [], 0, new(0, 0), [], new(0, 0)),
        new Dictionary<int, AssetCatalogEntry>());

    public static ProjectSoundState Create(
        GameId sourceGame,
        int level,
        byte[] bankBytes,
        SoundBankInventory bank,
        SoundRemapInventory remap,
        IReadOnlyDictionary<int, AssetCatalogEntry> mobyAssets)
    {
        ArgumentNullException.ThrowIfNull(bankBytes);
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(remap);
        ArgumentNullException.ThrowIfNull(mobyAssets);
        var bankSha256 = Convert.ToHexString(SHA256.HashData(bankBytes)).ToLowerInvariant();
        var samples = bank.Samples.GroupBy(sample => sample.ContentId, StringComparer.Ordinal).Select(group =>
        {
            var sample = group.First();
            var encoded = bankBytes.AsSpan(
                sample.Provenance.Offset, sample.Provenance.Length).ToArray();
            if (group.Any(value => !bankBytes.AsSpan(
                    value.Provenance.Offset, value.Provenance.Length).SequenceEqual(encoded)))
                throw new InvalidDataException(
                    $"Sound sample identity {sample.ContentId} has conflicting encoded payloads.");
            return new ProjectSoundSample(
                new(ProjectSounds.SampleId(encoded), AssetKind.SoundSample),
                sample.ContentId,
                group.Select(value => Provenance(
                    sourceGame, level, bankSha256, value.EncodedOffset, value.Provenance)).ToArray(),
                sample.EncodedLength,
                sample.DecodedFrameCount,
                sample.IsLooping,
                sample.LoopStartFrame,
                sample.LoopEndFrame);
        }).ToArray();
        var samplesBySourceId = samples.ToDictionary(value => value.SourceContentId, StringComparer.Ordinal);
        var programAssets = bank.Programs.ToDictionary(
            value => value.NativeIndex,
            value => value.ContentId is null
                ? null
                : new ProjectAssetReference(ProjectSounds.ProgramId(value.ContentId), AssetKind.Sound));
        var programs = bank.Programs.Select(program => new ProjectSoundProgram(
            programAssets[program.NativeIndex],
            program.ContentId,
            Provenance(sourceGame, level, bankSha256, program.NativeIndex, program.Provenance),
            program.Volume,
            program.VolumeGroup,
            program.Pan,
            program.InstanceLimit,
            program.Flags,
            program.Operations.Select(operation => new ProjectSoundOperation(
                operation.NativeIndex,
                operation.Type,
                operation.Argument0,
                operation.Argument1,
                operation.Argument2,
                operation.Delay,
                Range(operation.Provenance),
                operation.Tone is not { } tone ? null : new(
                    tone.Priority,
                    tone.Volume,
                    tone.CenterNote,
                    tone.CenterFine,
                    tone.Pan,
                    tone.NoteLow,
                    tone.NoteHigh,
                    tone.PitchBendLow,
                    tone.PitchBendHigh,
                    tone.Adsr1,
                    tone.Adsr2,
                    tone.Flags,
                    tone.Reserved,
                    samplesBySourceId[tone.SampleId].Asset),
                operation.ReferencedProgramIndex,
                operation.ReferencedProgramIndex is { } target
                    ? programAssets[target]
                    : null,
                operation.ParameterBytes.ToArray())).ToArray(),
            program.PreviewSupported,
            program.PreviewBlocker)).ToArray();

        var programsByIndex = programs.ToDictionary(value => value.Provenance.NativeIndex);
        var mobyRemaps = remap.Mobys.Select(map => new ProjectMobySoundMap(
            map.DefinitionIndex,
            map.ClassId,
            mobyAssets.TryGetValue(map.ClassId, out var moby)
                ? new(moby.Id, AssetKind.Moby)
                : null,
            map.RawListOffset,
            Range(map.ConfigurationProvenance),
            map.Slots.Select(slot => RemapSlot(slot, programsByIndex)).ToArray())).ToArray();
        return new(
            ProjectSoundSchema.CurrentVersion,
            programs,
            samples,
            mobyRemaps,
            remap.Globals.Select(slot => RemapSlot(slot, programsByIndex)).ToArray(),
            remap.OtherRecordCount,
            Range(remap.OtherRecordsProvenance),
            Range(remap.Provenance));
    }

    private static ProjectSoundRemapSlot RemapSlot(
        SoundRemapSlotInfo slot,
        IReadOnlyDictionary<int, ProjectSoundProgram> programs)
    {
        if (slot.NativeProgramIndex == -1)
            return new(slot.Slot, -1, null, Range(slot.Provenance), null);
        var program = programs[slot.NativeProgramIndex];
        return program.Asset is { } asset
            ? new(slot.Slot, slot.NativeProgramIndex, asset, Range(slot.Provenance), null)
            : new(slot.Slot, slot.NativeProgramIndex, null, Range(slot.Provenance),
                $"Program {slot.NativeProgramIndex} is not importable: {program.PreviewBlocker}");
    }

    private static ProjectSoundProvenance Provenance(
        GameId sourceGame,
        int level,
        string bankSha256,
        int nativeIndex,
        SoundSourceRange range) => new(
            ProjectSoundOrigin.Base,
            sourceGame.ToString(),
            level,
            bankSha256,
            nativeIndex,
            Range(range));

    private static ProjectSoundSourceRange Range(SoundSourceRange value) =>
        new(value.Offset, value.Length);
}

using RatchetPs2.Core.Sounds;

namespace Forge.Host.Domain;

internal static class CatalogSoundAssets
{
    public static IReadOnlyList<AssetCatalogPut> Create(byte[] bytes, SoundBankInventory bank,
        AssetSource source, string importer)
    {
        var bankId = AssetId.Compute(AssetKind.SoundBank, SoundBankAssetSchema.CanonicalFormatVersion, bytes);
        string[] tags = ["vanilla", $"game:{source.Game}", $"level:{source.Level[5..]}"];
        var results = new List<AssetCatalogPut>
        {
            new(AssetKind.SoundBank, SoundBankAssetSchema.CanonicalFormatVersion, bytes,
                new(importer, source, [$"sound-bank:{source.Level}"], [.. tags, "sound-bank"])),
        };
        var samples = bank.Samples.GroupBy(value => value.ContentId, StringComparer.Ordinal).ToDictionary(
            group => group.Key, group =>
            {
                var sample = group.First();
                var data = bytes.AsSpan(sample.Provenance.Offset, sample.Provenance.Length).ToArray();
                return (Sample: sample, Bytes: data, Id: ProjectSounds.SampleId(data));
            }, StringComparer.Ordinal);
        foreach (var sample in samples.Values)
            results.Add(new(AssetKind.SoundSample, ProjectSoundSchema.SampleCanonicalFormatVersion, sample.Bytes,
                new(importer, source with { SourceIndex = sample.Sample.EncodedOffset },
                    [$"sound-sample:{sample.Sample.ContentId[..12]}"], [.. tags, "sound-sample"])));
        var programs = bank.Programs.ToDictionary(value => value.NativeIndex,
            value => value.ContentId is null ? (AssetId?)null : ProjectSounds.ProgramId(value.ContentId));
        foreach (var program in bank.Programs.Where(value => value.ContentId is not null)
            .GroupBy(value => value.ContentId, StringComparer.Ordinal).Select(group => group.First()))
        {
            var referenced = program.Operations.Where(value => value.ReferencedProgramIndex is not null)
                .Select(value => programs[value.ReferencedProgramIndex!.Value]).OfType<AssetId>().ToArray();
            var referencedSamples = program.SampleIds.Select(id => samples[id].Id).ToArray();
            results.Add(new(AssetKind.Sound, ProjectSoundSchema.ProgramCanonicalFormatVersion,
                Convert.FromHexString(program.ContentId!), new(importer, source with
                {
                    SourceIndex = program.NativeIndex, SoundProgram = new(bankId, referenced, referencedSamples),
                }, [$"sound:{program.NativeIndex}"], [.. tags, "sound-program"])));
        }
        return results;
    }

    public static AssetMobySoundUse MobyUse(AssetId bankId,
        IReadOnlyDictionary<int, SoundProgramInfo> programsByIndex, int classId, IReadOnlyList<int> programs)
    {
        return new(bankId, programs.Select((index, slot) => (index, slot)).Where(value => value.index >= 0)
            .Select(value => programsByIndex.TryGetValue(value.index, out var program)
                ? new AssetSoundSlot(value.slot, value.index,
                    program.ContentId is null ? null : ProjectSounds.ProgramId(program.ContentId), program.PreviewSupported, program.PreviewBlocker)
                : new(value.slot, value.index, null, false, $"Program {value.index} is missing from the source bank.")).ToArray(), classId);
    }
}

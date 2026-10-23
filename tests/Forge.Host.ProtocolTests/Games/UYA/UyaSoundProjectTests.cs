using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Sounds;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaSoundProjectTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-uya-sound-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var fixture = CreateFixture();
            VerifyProjectionAndReferences(fixture);
            VerifyDependencyDiagnostics(fixture.State);
            VerifyCorpusProjectionWhenAvailable(root);
            await VerifyCorpusCompositionWhenAvailableAsync(root);
            await VerifyPersistenceCleanupRecoveryAndTransferAsync(root, fixture);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifyCorpusCompositionWhenAvailableAsync(string root)
    {
        var corpusRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../../../ratchet-ps2-cli/test-assets/extractions_uya"));
        var targetRoot = Path.Combine(corpusRoot, "level03_iso_world01");
        var donorRoot = Path.Combine(corpusRoot, "level41_iso_world01");
        var targetBankPath = Path.Combine(targetRoot, "level_wad", "sound.bnk");
        var targetHeaderPath = Path.Combine(targetRoot, "assets", "asset_header.bin");
        var donorBankPath = Path.Combine(donorRoot, "level_wad", "sound.bnk");
        var donorHeaderPath = Path.Combine(donorRoot, "assets", "asset_header.bin");
        if (new[] { targetBankPath, targetHeaderPath, donorBankPath, donorHeaderPath }
            .Any(path => !File.Exists(path))) return;

        var targetBankBytes = await File.ReadAllBytesAsync(targetBankPath);
        var targetHeaderBytes = await File.ReadAllBytesAsync(targetHeaderPath);
        var donorBankBytes = await File.ReadAllBytesAsync(donorBankPath);
        var targetBank = SoundBankReader.Inspect(GameId.UYA, targetBankBytes);
        var donorBank = SoundBankReader.Inspect(GameId.UYA, donorBankBytes);
        var target = SoundProjectService.Create(
            GameId.UYA, 3, targetBankBytes, targetBank,
            SoundBankReader.InspectRemap(GameId.UYA, targetHeaderBytes, targetBank.Programs.Count),
            new Dictionary<int, AssetCatalogEntry>());
        var donor = SoundProjectService.Create(
            GameId.UYA, 41, donorBankBytes, donorBank,
            SoundBankReader.InspectRemap(
                GameId.UYA, await File.ReadAllBytesAsync(donorHeaderPath), donorBank.Programs.Count),
            new Dictionary<int, AssetCatalogEntry>());
        VerifyDonorMobySoundClosure(target, donor, donorBankBytes, donorBank);
        var targetAssets = target.Programs.Where(value => value.Asset is not null)
            .Select(value => value.Asset!.Id).ToHashSet();
        var importedProgram = donor.Programs.First(value =>
            value.Asset is not null && !targetAssets.Contains(value.Asset.Id)
            && value.Operations is [{ Type: 1, Tone: not null }]);
        var remappedMoby = target.MobyRemaps.First(value => value.Slots.Count > 0);
        var plan = UyaMobySoundImportService.Prepare(
            target,
            donor,
            donorBankBytes,
            donorBank,
            new(
                AssetId.Compute(AssetKind.SoundBank, 1, donorBankBytes),
                [new(0, importedProgram.Provenance.NativeIndex, importedProgram.Asset!.Id, true, null)]),
            remappedMoby.ClassId,
            new(AssetId.Compute(AssetKind.Moby, 1, "donor moby"u8), AssetKind.Moby));
        var state = plan.State;
        importedProgram = state.Programs.Single(value => value.Asset == importedProgram.Asset);
        var projectPath = Path.Combine(root, "composition");
        var workspace = await CreateWorkspaceAsync(projectPath);
        await workspace.SetSoundsAsync(target);
        var unchanged = await UyaSoundCompositionService.ComposeAsync(
            workspace, targetBankBytes, targetHeaderBytes, CancellationToken.None);
        Equal(false, unchanged.BankChanged, "no-edit sound composition retains the source bank");
        Equal(targetBankBytes, unchanged.BankBytes, "no-edit sound composition is byte exact");
        await workspace.SetSoundsAsync(state, plan.Attachments);

        var composition = await UyaSoundCompositionService.ComposeAsync(
            workspace, targetBankBytes, targetHeaderBytes, CancellationToken.None);
        var output = SoundBankReader.Inspect(GameId.UYA, composition.BankBytes);
        Equal(true, composition.BankChanged, "imported sound marks the target bank changed");
        Equal(targetBank.Programs.Count + 1, output.Programs.Count,
            "sound composition appends one canonical donor program");
        Equal(importedProgram.SourceContentId, output.Programs[^1].ContentId,
            "sound composition semantically re-reads to the donor program identity");
        Equal(new[] { importedProgram.Asset!.Id }, composition.Plan.AppendedPrograms.ToArray(),
            "sound composition preflight identifies the appended donor program");
        Equal(output.Programs.Count - 1, composition.Plan.ProgramSlots[importedProgram.Asset.Id],
            "sound composition preflight assigns the final deterministic slot");
        Equal(true, composition.RemapChanged, "imported Moby sound marks the target remap changed");
        var composedHeader = SoundBankComposer.ComposeRemap(
            GameId.UYA, targetHeaderBytes, composition.Remap);
        var composedRemap = SoundBankReader.InspectRemap(
            GameId.UYA, composedHeader, output.Programs.Count);
        Equal(output.Programs.Count - 1,
            composedRemap.Mobys.Single(value => value.ClassId == remappedMoby.ClassId)
                .Slots[0].NativeProgramIndex,
            "sound composition rewrites a donor Moby slot to its final appended program index");
    }

    private static void VerifyDonorMobySoundClosure(
        ProjectSoundState target,
        ProjectSoundState donor,
        byte[] donorBankBytes,
        SoundBankInventory donorBank)
    {
        var targetPrograms = target.Programs.Where(value => value.Asset is not null)
            .Select(value => value.Asset!.Id).ToHashSet();
        var source = donor.MobyRemaps.First(value => value.Slots.Any(slot =>
            slot.Sound is { } sound && !targetPrograms.Contains(sound.Id)));
        var donorPrograms = donor.Programs.ToDictionary(value => value.Provenance.NativeIndex);
        var slots = source.Slots.Select(value =>
        {
            if (value.NativeProgramIndex < 0)
                return new AssetSoundSlot(value.Slot, -1, null, false, null);
            var program = donorPrograms[value.NativeProgramIndex];
            return new AssetSoundSlot(
                value.Slot,
                value.NativeProgramIndex,
                program.Asset?.Id,
                program.PreviewSupported,
                program.PreviewBlocker);
        }).ToArray();
        var plan = UyaMobySoundImportService.Prepare(
            target,
            donor,
            donorBankBytes,
            donorBank,
            new(AssetId.Compute(AssetKind.SoundBank, 1, donorBankBytes), slots, source.ClassId),
            source.ClassId,
            new(AssetId.Compute(AssetKind.Moby, 1, "donor moby"u8), AssetKind.Moby));
        var importedMap = plan.State.MobyRemaps.Single(value => value.ClassId == source.ClassId);
        Equal(true, slots.Select(value => value.Sound)
            .SequenceEqual(importedMap.Slots.Select(value => value.Sound?.Id)),
            "donor Moby placement retains its complete sound-slot mapping");
        var importedPrograms = plan.State.Programs.Where(value => value.Asset is not null)
            .Select(value => value.Asset!.Id).ToHashSet();
        Equal(true, importedMap.Slots.Where(value => value.Sound is not null)
            .All(value => importedPrograms.Contains(value.Sound!.Id)),
            "donor Moby placement imports every mapped program");
        Equal(true, plan.State.Programs.Where(value => value.Asset is not null)
            .SelectMany(value => value.Operations)
            .Where(value => value.ReferencedProgram is not null)
            .All(value => importedPrograms.Contains(value.ReferencedProgram!.Id)),
            "donor Moby placement imports transitive child programs");
    }

    private static void VerifyCorpusProjectionWhenAvailable(string root)
    {
        var corpusRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../../../ratchet-ps2-cli/test-assets/extractions_uya"));
        if (!Directory.Exists(corpusRoot)) return;
        var checkedLevels = 0;
        foreach (var directory in Directory.EnumerateDirectories(corpusRoot, "level*_iso_world01"))
        {
            var bankPath = Path.Combine(directory, "level_wad", "sound.bnk");
            var headerPath = Path.Combine(directory, "assets", "asset_header.bin");
            if (!File.Exists(bankPath) || !File.Exists(headerPath)) continue;
            var digits = new string(Path.GetFileName(directory).Skip(5).TakeWhile(char.IsDigit).ToArray());
            var level = int.Parse(digits);
            var bankBytes = File.ReadAllBytes(bankPath);
            var bank = SoundBankReader.Inspect(GameId.UYA, bankBytes);
            var remap = SoundBankReader.InspectRemap(
                GameId.UYA, File.ReadAllBytes(headerPath), bank.Programs.Count);
            var state = SoundProjectService.Create(
                GameId.UYA, level, bankBytes, bank, remap, new Dictionary<int, AssetCatalogEntry>());
            ForgeProjectValidation.Validate(
                root, Manifest(root), EmptyContent() with { Sounds = state });
            Equal(bank.Samples.Select(value => value.ContentId).Distinct(StringComparer.Ordinal).Count(),
                state.Samples.Select(value => value.Asset).Distinct().Count(),
                $"level {level:00} projects each shared sample once");
            checkedLevels++;
        }
        if (checkedLevels > 0)
            Console.WriteLine($"Forge sound projection: validated {checkedLevels} bank/remap pair(s).");
    }

    private static void VerifyProjectionAndReferences(Fixture fixture)
    {
        Equal(2, fixture.State.Programs.Count, "sound inventory projects every playable program");
        Equal(1, fixture.State.Samples.Count, "shared encoded samples deduplicate in project state");
        Equal(fixture.State.Programs[0].Operations[0].Tone!.Sample,
            fixture.State.Programs[1].Operations[0].Tone!.Sample,
            "distinct programs retain the same shared sample dependency");
        Equal(fixture.State.Programs[0].Asset, fixture.State.Programs[1].Asset,
            "identical native program aliases reuse one stable sound asset identity");
        Equal(0x222, fixture.State.MobyRemaps[0].ClassId,
            "moby remaps retain definition order rather than sorting by class ID");
        Equal(fixture.State.Programs[1].Asset, fixture.State.MobyRemaps[0].Slots[0].Sound,
            "moby remap native indexes resolve to stable sound assets");
        Equal<ProjectAssetReference?>(null, fixture.State.MobyRemaps[1].Slots[0].Sound,
            "the proven -1 remap sentinel remains null");

        var content = EmptyContent() with { Sounds = fixture.State };
        var graph = ProjectReferenceGraph.Create(content);
        Equal(2, graph.Incoming(fixture.State.Samples[0].Asset.Id).Count,
            "reference index reports both program-to-sample edges");
        Equal(1, graph.Incoming(fixture.State.Programs[1].Asset!.Id).Count,
            "reference index reports the moby-to-program edge");
        Equal(true, graph.Incoming(fixture.State.Programs[1].Asset!.Id)[0].OwnerField.Contains("0x0222"),
            "inbound sound navigation identifies the exact moby slot owner");
    }

    private static void VerifyDependencyDiagnostics(ProjectSoundState state)
    {
        var secondSourceId = Hash("diagnostic-program-1");
        var second = state.Programs[1] with
        {
            SourceContentId = secondSourceId,
            Asset = new(ProjectSounds.ProgramId(secondSourceId), AssetKind.Sound),
        };
        state = state with
        {
            Programs = [state.Programs[0], second],
            MobyRemaps = state.MobyRemaps.Select(map => map with
            {
                Slots = map.Slots.Select(slot => slot.NativeProgramIndex == 1
                    ? slot with { Sound = second.Asset }
                    : slot).ToArray(),
            }).ToArray(),
        };
        var root = Path.Combine(Path.GetTempPath(), $"forge-sound-validation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var manifest = Manifest(root);
            var missing = AssetId.Compute(AssetKind.SoundSample, 1, "missing"u8);
            var first = state.Programs[0];
            var missingState = state with
            {
                Programs =
                [
                    first with
                    {
                        Operations =
                        [
                            first.Operations[0] with
                            {
                                Tone = first.Operations[0].Tone! with
                                {
                                    Sample = new(missing, AssetKind.SoundSample),
                                },
                            },
                        ],
                    },
                    state.Programs[1],
                ],
            };
            ThrowsContains<InvalidDataException>(() => ForgeProjectValidation.Validate(
                    root, manifest, EmptyContent() with { Sounds = missingState }),
                "operation[0].sample", "missing dependency identifies its exact graph edge");

            var wrongKind = state with
            {
                Programs =
                [
                    first with
                    {
                        Operations =
                        [
                            first.Operations[0] with
                            {
                                Tone = first.Operations[0].Tone! with
                                {
                                    Sample = new(state.Samples[0].Asset.Id, AssetKind.Sound),
                                },
                            },
                        ],
                    },
                    state.Programs[1],
                ],
            };
            ThrowsContains<InvalidDataException>(() => ForgeProjectValidation.Validate(
                    root, manifest, EmptyContent() with { Sounds = wrongKind }),
                "wrong-kind", "wrong-kind dependency identifies the owner edge");

            var left = state.Programs[0];
            var right = state.Programs[1];
            var cycle = state with
            {
                Programs =
                [
                    left with
                    {
                        Operations =
                        [
                            left.Operations[0] with
                            {
                                Tone = null,
                                NativeReferencedProgramIndex = right.Provenance.NativeIndex,
                                ReferencedProgram = right.Asset,
                            },
                        ],
                    },
                    right with
                    {
                        Operations =
                        [
                            right.Operations[0] with
                            {
                                Tone = null,
                                NativeReferencedProgramIndex = left.Provenance.NativeIndex,
                                ReferencedProgram = left.Asset,
                            },
                        ],
                    },
                ],
            };
            ThrowsContains<InvalidDataException>(() => ForgeProjectValidation.Validate(
                    root, manifest, EmptyContent() with { Sounds = cycle }),
                "recursive edge", "recursive sound dependencies identify the failing edge");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifyPersistenceCleanupRecoveryAndTransferAsync(
        string root,
        Fixture fixture)
    {
        var projectPath = Path.Combine(root, "project");
        var workspace = await CreateWorkspaceAsync(projectPath);
        var imported = fixture.State with
        {
            Samples = fixture.State.Samples.Select(value => value with
            {
                Provenance = value.Provenance.Select(provenance =>
                    provenance with { Origin = ProjectSoundOrigin.Imported }).ToArray(),
            }).ToArray(),
            Programs = fixture.State.Programs.Select(value => value with
            {
                Provenance = value.Provenance with { Origin = ProjectSoundOrigin.Imported },
            }).ToArray(),
        };
        var before = workspace.CurrentFingerprint;
        await workspace.SetSoundsAsync(imported,
        [
            new(AssetKind.SoundSample, ProjectSoundSchema.SampleCanonicalFormatVersion,
                fixture.SampleBytes),
        ]);
        var soundWorkspace = await SoundWorkspaceService.ReadAsync(workspace, CancellationToken.None);
        Equal(2, soundWorkspace.Programs.Count,
            "sound workspace lists every native program alias");
        Equal(1, soundWorkspace.SampleCount,
            "sound workspace reports deduplicated samples");
        Equal(new[] { 0 }, soundWorkspace.Programs[0].GlobalRemapSlots.ToArray(),
            "sound workspace reports global remap identity");
        Equal(0, soundWorkspace.Programs[0].MobyDependents.Count,
            "sound workspace does not conflate remaps for stable program aliases");
        Equal(0x222, soundWorkspace.Programs[1].MobyDependents.Single().ClassId,
            "sound workspace reports moby dependents");
        Equal(0, soundWorkspace.Programs[1].GlobalRemapSlots.Count,
            "sound workspace keeps remaps attached to their native alias");
        Equal(44_100, soundWorkspace.Programs[0].Samples.Single().SampleRate,
            "sound workspace reports effective preview rate");
        Equal(1, soundWorkspace.Programs[0].Samples.Single().Channels,
            "sound workspace reports mono channel metadata");
        Equal(true, soundWorkspace.Programs.All(value => value.PreviewSupported),
            "sound workspace exposes per-entry preview support");
        Equal(false, before == workspace.CurrentFingerprint,
            "typed sound state participates in project fingerprints");
        Equal(true, workspace.IsAssetReferenced(imported.Samples[0].Asset.Id),
            "sound sample dependencies are protected from cleanup");
        var recovery = await workspace.WriteRecoveryAsync()
            ?? throw new InvalidOperationException("dirty sound state did not produce a recovery snapshot");
        await workspace.SetSoundsAsync(ProjectSoundState.Empty);
        await workspace.SaveAsync();
        Equal(0, (await workspace.CollectUnreferencedAssetsAsync()).Count,
            "a recovery snapshot retains imported sound blobs");
        await workspace.LoadRecoveryAsync(recovery.Id);
        Equal(imported.Programs.Select(value => value.Asset).ToArray(),
            workspace.Content.Sounds.Programs.Select(value => value.Asset).ToArray(),
            "sound identities survive recovery");
        var encoded = await workspace.ReadAttachedAssetVerifiedAsync(
            imported.Samples[0].Asset, ForgeProjectWorkspace.MaxAttachedAssetBytes);
        Equal(fixture.SampleBytes, encoded, "recovered encoded sample passes attachment integrity");
        await workspace.SaveAsync();

        var archive = Path.Combine(root, "project.zip");
        ZipFile.CreateFromDirectory(projectPath, archive, CompressionLevel.Fastest, false);
        var transferredPath = Path.Combine(root, "transferred");
        ZipFile.ExtractToDirectory(archive, transferredPath);
        var transferred = await ForgeProjectWorkspace.OpenAsync(transferredPath);
        Equal(imported.Programs.Select(value => value.Asset).ToArray(),
            transferred.Content.Sounds.Programs.Select(value => value.Asset).ToArray(),
            "ZIP transfer preserves typed sound identities");
        Equal(fixture.SampleBytes, await transferred.ReadAttachedAssetVerifiedAsync(
                imported.Samples[0].Asset, ForgeProjectWorkspace.MaxAttachedAssetBytes),
            "ZIP transfer preserves imported encoded sample blobs");

        var cleanupPath = Path.Combine(root, "cleanup");
        var cleanup = await CreateWorkspaceAsync(cleanupPath);
        await cleanup.SetSoundsAsync(imported,
        [
            new(AssetKind.SoundSample, ProjectSoundSchema.SampleCanonicalFormatVersion,
                fixture.SampleBytes),
        ]);
        await cleanup.SaveAsync();
        await cleanup.SetSoundsAsync(ProjectSoundState.Empty);
        await cleanup.SaveAsync();
        Equal(new[] { imported.Samples[0].Asset.Id },
            (await cleanup.CollectUnreferencedAssetsAsync()).ToArray(),
            "cleanup removes an encoded sample after its sound graph is removed");
    }

    private static Fixture CreateFixture()
    {
        var sampleBytes = new byte[16];
        sampleBytes[1] = 1;
        var sourceSampleId = Hash("sample-source");
        var sample = new SoundSampleInfo(
            sourceSampleId, 0, sampleBytes.Length, 28, false, null, 28, new(128, sampleBytes.Length));
        var tone = new SoundToneInfo(
            0, 127, 60, 0, 0, 0, 127, 0, 0, 0, 0, 0, 0, 0, sourceSampleId);
        SoundProgramInfo Program(int index) => new(
            index, Hash("shared-program"), 127, 0, 0, 1, 0, new(24 + index * 12, 12),
            [new(index, 1, 0, 0, 0, 0, new(64 + index * 8, 8), new(96 + index * 24, 24), new byte[24], tone, null)],
            [sourceSampleId], true, null);
        var bank = new SoundBankInventory(
            "989snd-sblk", 3, 0x574144, new(24, 100), new(128, 16),
            [Program(0), Program(1)], [sample]);
        var remap = new SoundRemapInventory(
            new(256, 80),
            [
                new(0, 0x222, 40, 1, new(264, 4), new(300, 4),
                    [new(0, 1, new(300, 4))]),
                new(1, 0x111, 44, 1, new(268, 4), new(304, 4),
                    [new(0, -1, new(304, 4))]),
            ],
            1,
            new(272, 32),
            [new(0, 0, new(308, 4))],
            new(308, 4));
        var bankBytes = new byte[512];
        sampleBytes.CopyTo(bankBytes, 128);
        var mobys = new Dictionary<int, AssetCatalogEntry>
        {
            [0x222] = CatalogMoby(0x222),
            [0x111] = CatalogMoby(0x111),
        };
        return new(SoundProjectService.Create(
            GameId.UYA, 3, bankBytes, bank, remap, mobys), sampleBytes);
    }

    private static AssetCatalogEntry CatalogMoby(int classId) => new(
        AssetId.Compute(AssetKind.Moby, 1, BitConverter.GetBytes(classId)),
        AssetKind.Moby, 1, 4, [], [], [], DateTimeOffset.UnixEpoch, "test");

    private static Task<ForgeProjectWorkspace> CreateWorkspaceAsync(string path) =>
        ForgeProjectWorkspace.CreateAsync(
            path,
            "Sound test",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
            new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)),
            []);

    private static ForgeProjectManifest Manifest(string root) => new(
        ProjectSchema.CurrentVersion,
        ProjectSchema.ManifestDocumentType,
        EntityId.New(),
        "Sound validation",
        new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
        new("UYA", "NTSC-U", "1.00", 3, new string('a', 32)),
        ForgeProjectWorkspace.DefaultContentPath);

    private static ForgeProjectContent EmptyContent() => new(
        ProjectSchema.CurrentVersion,
        ProjectSchema.ContentDocumentType,
        [], [], [], []);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void ThrowsContains<T>(Action action, string expected, string context) where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception) when (exception.Message.Contains(expected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        throw new InvalidOperationException($"{context}: expected {typeof(T).Name} containing '{expected}'");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (expected is Array expectedArray && actual is Array actualArray)
        {
            if (expectedArray.Cast<object>().SequenceEqual(actualArray.Cast<object>())) return;
        }
        else if (EqualityComparer<T>.Default.Equals(expected, actual))
        {
            return;
        }
        throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    private sealed record Fixture(ProjectSoundState State, byte[] SampleBytes);
}

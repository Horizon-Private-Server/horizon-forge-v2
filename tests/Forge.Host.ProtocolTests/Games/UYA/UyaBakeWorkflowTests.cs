using Forge.Host.Games.UYA;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Gameplay;
using RatchetPs2.Core.IO;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Textures.Palettes;
using RatchetPs2.Core.Textures.Pif;
using RatchetPs2.Core.Wad;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Games.DL.Level;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Games.UYA.Level;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaBakeWorkflowTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-uya-bake-workflow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "catalog"));
            await PutAsync(catalog, AssetKind.Moby, 100, 0x11);
            var baseTie = await PutAsync(catalog, AssetKind.Tie, 200, 0x22);
            await PutAsync(catalog, AssetKind.Shrub, 300, 0x33);
            var iso = UyaProjectTests.CreateIso();
            var project = Path.Combine(root, "project");
            await UyaProjectService.CreateValidatedAsync(
                new MemoryStream(iso, writable: false),
                catalog,
                new("synthetic.iso", catalog.RootPath, project, "Bake workflow", new string('a', 32), "1.00", 3, true));
            var context = new BakeFingerprintContext(
                new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"), "translator-1", "baker-1");
            using var isoStream = new MemoryStream(iso, writable: false);
            var sourceLevelWad = UyaLooseLevelWadExtractor.ExtractPrimary(isoStream, 3).Bytes;

            var unbaked = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(false, unbaked.Succeeded, "unbaked staging cannot pack");
            Equal(null, unbaked.OutputBytes, "failed pack exposes no output");

            var first = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, first.Succeeded, "initial bake succeeds");
            Equal(10, first.WrittenLayers.Count, "initial bake writes every layer");
            Equal(true, first.Validation.Plan.Layers.All(value => value.State == BakeLayerState.Clean),
                "initial bake validates clean");
            var paletteReport = first.PaletteReport!;
            Equal(paletteReport, first.Manifest.PaletteReport, "bake result exposes its staged palette report");
            Equal(3, paletteReport.InputTextureCount, "palette report input texture count");
            Equal(3, paletteReport.InputPaletteCount, "palette report distinct input palette count");
            Equal(1, paletteReport.OutputPaletteCount, "palette report optimized palette count");
            Equal(3_072L, paletteReport.InputPaletteBytes, "palette report input VRAM estimate");
            Equal(1_024L, paletteReport.OutputPaletteBytes, "palette report output VRAM estimate");
            Equal(2_048L, paletteReport.EstimatedPaletteVramSavingsBytes, "palette report VRAM savings estimate");
            Equal(true, paletteReport.IsLossless, "vanilla palette report is lossless");
            Equal(0d, paletteReport.ImportedQuantizationError, "vanilla palette report quantization error");
            Equal(PaletteOptimizer.ExactMethod, paletteReport.Optimization.Method,
                "palette report labels its optimization method");
            Equal(true, paletteReport.Optimization.IsProvenOptimal, "small palette report is proven optimal");
            Equal(3, paletteReport.Optimization.Assignments.Count, "palette report traces every texture assignment");
            Equal(6, paletteReport.Optimization.Assignments.Sum(value => value.IndexRemaps.Count),
                "palette report traces every referenced old-to-new index mapping");
            Equal("e4a4b98283e7ca9a5ebff40ba1683e3beb34955f92422aba2b254f14276e9d0d", Convert.ToHexString(SHA256.HashData(
                ForgeProjectPersistence.Serialize(paletteReport))).ToLowerInvariant(),
                "palette report schema snapshot");
            var textureInventory = await UyaTextureInventoryService.BuildAsync(project, catalog);
            Equal(3, textureInventory.Textures.Count, "staged vanilla definitions inventory their textures");
            Equal(12L, textureInventory.TexelCount, "texture inventory counts every selected vanilla texel");
            var optimizedPalettes = PaletteOptimizer.Optimize(textureInventory);
            Equal(0, optimizedPalettes.Violations.Count, "staged vanilla textures optimize without violations");
            Equal(1, optimizedPalettes.Palettes.Count, "compatible staged vanilla textures share one exact palette");
            Equal(3, optimizedPalettes.Assignments.Count, "every staged vanilla texture receives a palette assignment");
            var staleReport = paletteReport with
            {
                InputPaletteCount = 2,
                InputPaletteBytes = 2_048,
                EstimatedPaletteVramSavingsBytes = 1_024,
            };
            var staging = await BakeStagingStore.OpenAsync(project);
            await staging.RestoreManifestAsync(first.Manifest with { PaletteReport = staleReport });
            var stalePack = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(false, stalePack.Succeeded, "stale palette report cannot accompany packed output");
            Equal(true, stalePack.Diagnostics.Any(value => value.Cause.Contains(
                "palette report does not match", StringComparison.Ordinal)), "stale palette report is actionable");
            await staging.RestoreManifestAsync(first.Manifest);
            var packedSource = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(true, packedSource.Succeeded, "validated staging packs: "
                + string.Join(" | ", packedSource.Diagnostics.Select(value => value.Cause)));
            Equal("350c6a0fe2801b7b62f5993e9324997cd9288af5d954853c5b43745c836fb128",
                packedSource.OutputSha256, "unchanged staged project golden WAD");
            _ = UyaLevelWadInventoryReader.Read(packedSource.OutputBytes!);
            var installed = ReadAssets(packedSource.OutputBytes!);
            Equal(0x11, installed.AssetWad[installed.Mobys.Single().ModelOffset], "selected moby model is installed");
            Equal(0x22, installed.AssetWad[installed.Ties.Single().ModelOffset], "selected tie model is installed");
            Equal(0x33, installed.AssetWad[installed.Shrubs.Single().ModelOffset], "selected shrub model is installed");
            var paletteIds = new[]
            {
                DlAssetReader.ReadTextureDefinitions(installed.Source.HeaderBytes,
                    installed.Header.MobyTextureOffset, installed.Header.MobyTextureCount).Single().PaletteId,
                DlAssetReader.ReadTextureDefinitions(installed.Source.HeaderBytes,
                    installed.Header.TieTextureOffset, installed.Header.TieTextureCount).Single().PaletteId,
                DlAssetReader.ReadTextureDefinitions(installed.Source.HeaderBytes,
                    installed.Header.ShrubTextureOffset, installed.Header.ShrubTextureCount).Single().PaletteId,
            };
            Equal(1, paletteIds.Distinct().Count(), "installed moby, tie, and shrub textures share one palette");

            var workspace = await ForgeProjectWorkspace.OpenAsync(project);
            var spline = workspace.Content.Entities.Single(value => value.Geometry?.Spline is not null);
            var editedSplinePoints = spline.Geometry!.Spline!.Points
                .Select((value, index) => index == 0 ? value with { W = value.W + 1 } : value).ToArray();
            workspace.UpdateSplinePoints(spline.EntityId, editedSplinePoints);
            await workspace.SaveAsync();
            var splineBake = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, splineBake.WrittenLayers.Select(value => value.Layer).SequenceEqual([BakeLayerId.Gameplay]),
                "spline edit rebuilds only gameplay");
            var packedSpline = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(true, packedSpline.Succeeded, "edited spline staging packs: "
                + string.Join(" | ", packedSpline.Diagnostics.Select(value => value.Cause)));
            var packedSplineBytes = UyaLevelWadUnpacker.Unpack(packedSpline.OutputBytes!).Files
                .Single(value => value.Path == "gameplay/core/splines.bin").Bytes;
            Equal(editedSplinePoints[0].W, GameplayGeometryReader.ReadSplines(packedSplineBytes).Single().Points[0].W,
                "packed WAD preserves edited spline data");

            var crossLevelTie = await PutAsync(catalog, AssetKind.Tie, 200, 0x44, "level45");
            var contentPath = (await ForgeProjectWorkspace.OpenAsync(project)).ContentFilePath;
            await File.WriteAllTextAsync(contentPath,
                (await File.ReadAllTextAsync(contentPath)).Replace(
                    baseTie.Id.ToString(), crossLevelTie.Id.ToString(), StringComparison.Ordinal));
            var crossLevelBake = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, crossLevelBake.WrittenLayers.Select(value => value.Layer)
                .SequenceEqual([BakeLayerId.Ties, BakeLayerId.Lighting]),
                "cross-level tie asset invalidates only ties and lighting");
            var crossLevelPack = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(true, crossLevelPack.Succeeded, "cross-level tie asset packs");
            var crossLevelAssets = ReadAssets(crossLevelPack.OutputBytes!);
            Equal(0x44, crossLevelAssets.AssetWad[crossLevelAssets.Ties.Single().ModelOffset],
                "cross-level selected tie model replaces the base-level definition");

            using (var packCancellation = new CancellationTokenSource())
            {
                await ThrowsAsync<OperationCanceledException>(() => UyaLevelPackService.PackAsync(
                    project,
                    catalog,
                    sourceLevelWad,
                    context,
                    progress: new InlineProgress<LevelArchiveProgress>(value =>
                    {
                        if (value.Phase == LevelArchivePhase.Inventory) packCancellation.Cancel();
                    }),
                    cancellationToken: packCancellation.Token));
            }

            var firstManifest = ManifestBytes(crossLevelBake.Manifest);
            var noOp = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, noOp.IsCurrent, "no-op bake reports current staging");
            Equal(0, noOp.WrittenLayers.Count, "no-op bake writes no layers");
            Equal(true, firstManifest.SequenceEqual(ManifestBytes(noOp.Manifest)),
                "no-op bake preserves manifest bytes");

            workspace = await ForgeProjectWorkspace.OpenAsync(project);
            var tie = workspace.Content.Entities.Single(value => value.Layer == "ties");
            workspace.UpdateTransform(tie.EntityId, tie.Transform with
            {
                Position = tie.Transform.Position with { X = tie.Transform.Position.X + 1 },
            });
            await workspace.SaveAsync();
            var incremental = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, incremental.WrittenLayers.Select(value => value.Layer)
                .SequenceEqual([BakeLayerId.Ties, BakeLayerId.Lighting]),
                "tie edit rebuilds only ties and lighting");
            foreach (var prior in noOp.Manifest.Layers.Where(value => value.Layer is not BakeLayerId.Ties and not BakeLayerId.Lighting))
                Equal(prior, incremental.Manifest.Layers.Single(value => value.Layer == prior.Layer),
                    $"tie edit preserves {prior.Layer} snapshot");
            var packedEdit = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(true, packedEdit.Succeeded, "incremental staging packs");
            var packedTieBytes = UyaLevelWadUnpacker.Unpack(packedEdit.OutputBytes!).Files
                .Single(value => value.Path == "gameplay/core/tie_instances.bin").Bytes;
            Equal(41f, UyaTieInstancesReader.Read(packedTieBytes).Instances.Single().Transform.Position.X,
                "packed WAD preserves staged tie translation");

            workspace = await ForgeProjectWorkspace.OpenAsync(project);
            tie = workspace.Content.Entities.Single(value => value.Layer == "ties");
            workspace.UpdateTransform(tie.EntityId, tie.Transform with
            {
                Position = tie.Transform.Position with { X = tie.Transform.Position.X + 1 },
            });
            await workspace.SaveAsync();
            var deferred = new HashSet<BakeLayerId> { BakeLayerId.Ties, BakeLayerId.Lighting };
            var selective = await UyaBakeService.BakeAsync(
                project, catalog, context, includedLayers: new HashSet<BakeLayerId>());
            Equal(false, selective.IsCurrent, "selective bake reports deferred layers");
            Equal(0, selective.WrittenLayers.Count, "selective bake leaves unchecked layers staged");
            var packedDeferred = await UyaLevelPackService.PackAsync(
                project, catalog, sourceLevelWad, context, deferredLayers: deferred);
            Equal(true, packedDeferred.Succeeded, "deferred staging packs");
            var deferredTieBytes = UyaLevelWadUnpacker.Unpack(packedDeferred.OutputBytes!).Files
                .Single(value => value.Path == "gameplay/core/tie_instances.bin").Bytes;
            Equal(41f, UyaTieInstancesReader.Read(deferredTieBytes).Instances.Single().Transform.Position.X,
                "deferred tie layer preserves its last staged transform");
            await UyaBakeService.BakeAsync(project, catalog, context);

            var baseInspection = await UyaBaseLayerStore.InspectAsync(project, catalog);
            var baseManifest = baseInspection.Manifest!;
            var skyRecord = baseManifest.Layers.Single(value => value.Layer == BakeLayerId.Sky);
            var sourceSky = skyRecord.Assets.Single();
            var sourceSkyEntry = catalog.Query(new(Id: sourceSky.Asset.Id)).Single();
            var editedSky = await File.ReadAllBytesAsync(catalog.ResolveBlobPath(sourceSky.Asset.Id)!);
            Array.Resize(ref editedSky, editedSky.Length + 0x10);
            var editedSkyEntry = await catalog.PutAsync(AssetKind.Sky, sourceSky.CanonicalFormatVersion, editedSky,
                new("test-edit", sourceSkyEntry.Sources.Single(), sourceSkyEntry.Aliases, sourceSkyEntry.Tags));
            var baseManifestPath = Path.Combine(project, UyaBaseLayerStore.RelativeManifestPath);
            var originalManifestText = await File.ReadAllTextAsync(baseManifestPath);
            await File.WriteAllTextAsync(baseManifestPath,
                originalManifestText.Replace(
                    sourceSky.Asset.Id.ToString(), editedSkyEntry.Id.ToString(), StringComparison.Ordinal));
            var assetBake = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, assetBake.WrittenLayers.Select(value => value.Layer).SequenceEqual([BakeLayerId.Sky]),
                "resized sky rebuilds only its bake layer");
            var packedAssetEdit = await UyaLevelPackService.PackAsync(project, catalog, sourceLevelWad, context);
            Equal(true, packedAssetEdit.Succeeded, "resized asset-WAD payload packs: "
                + string.Join(" | ", packedAssetEdit.Diagnostics.Select(value => value.Cause)));
            var packedAssetFiles = UyaLevelWadRenderPackageBuilder.ReadAssetSourceFiles(
                UyaLevelWadUnpacker.Unpack(packedAssetEdit.OutputBytes!).Files);
            var packedAssetWad = BinaryMagic.IsWad(packedAssetFiles.AssetWadBytes)
                ? WadCompression.Decompress(packedAssetFiles.AssetWadBytes)
                : packedAssetFiles.AssetWadBytes;
            var packedHeader = DlAssetReader.ReadHeader(packedAssetFiles.HeaderBytes);
            var packedMobys = DlAssetReader.ReadModelDefinitions(
                packedAssetFiles.HeaderBytes, packedHeader.MobyModelOffset, packedHeader.MobyModelCount);
            var packedTies = DlAssetReader.ReadModelDefinitions(
                packedAssetFiles.HeaderBytes, packedHeader.TieModelOffset, packedHeader.TieModelCount);
            var packedShrubs = DlAssetReader.ReadShrubDefinitions(
                packedAssetFiles.HeaderBytes, packedHeader.ShrubModelOffset, packedHeader.ShrubModelCount);
            var packedOffsets = DlAssetReader.CollectKnownAssetOffsets(
                GameId.UYA, packedHeader, packedAssetWad.Length, packedMobys, packedTies, packedShrubs);
            var packedSky = DlAssetReader.ReadAssetSlice(packedAssetWad, packedHeader.SkyOffset, packedOffsets);
            Equal(true, packedSky.SequenceEqual(editedSky), "packed WAD preserves resized sky payload");

            workspace = await ForgeProjectWorkspace.OpenAsync(project);
            var shrub = workspace.Content.Entities.Single(value => value.Layer == "shrubs");
            workspace.UpdateTransform(shrub.EntityId, shrub.Transform with
            {
                Position = shrub.Transform.Position with { X = shrub.Transform.Position.X + 1 },
            });
            await workspace.SaveAsync();
            var beforeCancel = ManifestBytes(assetBake.Manifest);
            using (var cancellation = new CancellationTokenSource())
            {
                await ThrowsAsync<OperationCanceledException>(() => UyaBakeService.BakeAsync(
                    project,
                    catalog,
                    context,
                    progress: value =>
                    {
                        if (value.Phase == UyaBakePhase.Staging
                            && value.Layer == BakeLayerId.Shrubs
                            && value.CompletedLayers > 0)
                            cancellation.Cancel();
                        return ValueTask.CompletedTask;
                    },
                    cancellationToken: cancellation.Token));
            }
            var cancelledManifest = (await BakeStagingStore.OpenAsync(project)).Manifest;
            Equal(true, beforeCancel.SequenceEqual(ManifestBytes(cancelledManifest)),
                "cancelled bake restores prior manifest");

            var afterCancel = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, afterCancel.Succeeded, "cancelled bake retries successfully");
            workspace = await ForgeProjectWorkspace.OpenAsync(project);
            tie = workspace.Content.Entities.Single(value => value.Layer == "ties");
            workspace.UpdateTransform(tie.EntityId, tie.Transform with
            {
                Position = tie.Transform.Position with { Y = tie.Transform.Position.Y + 1 },
            });
            await workspace.SaveAsync();
            var lightingAsset = (await UyaBaseLayerStore.InspectAsync(project, catalog)).Manifest!.Layers
                .Single(value => value.Layer == BakeLayerId.Lighting).Assets
                .Single(value => value.Name == "directional-lights.bin");
            var lightingPath = catalog.ResolveBlobPath(lightingAsset.Asset.Id)!;
            var beforeFailure = ManifestBytes(afterCancel.Manifest);
            await ThrowsAsync<InvalidDataException>(() => UyaBakeService.BakeAsync(
                project,
                catalog,
                context,
                progress: value =>
                {
                    if (value.Phase == UyaBakePhase.Staging
                        && value.Layer == BakeLayerId.Ties
                        && value.CompletedLayers > 0
                        && File.Exists(lightingPath))
                        File.Delete(lightingPath);
                    return ValueTask.CompletedTask;
                }));
            var failedManifest = (await BakeStagingStore.OpenAsync(project)).Manifest;
            Equal(true, beforeFailure.SequenceEqual(ManifestBytes(failedManifest)),
                "failed bake restores prior manifest");

            await UyaProjectService.MigrateValidatedAsync(
                project, catalog, new MemoryStream(iso, writable: false));
            var recovered = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, recovered.Succeeded, "failed bake repairs and retries successfully");
            var repeated = await UyaBakeService.BakeAsync(project, catalog, context);
            Equal(true, repeated.IsCurrent, "repeated bake is current");
            Equal(true, ManifestBytes(recovered.Manifest)
                .SequenceEqual(ManifestBytes(repeated.Manifest)),
                "repeated bake is manifest deterministic");

            var physicalIso = iso.ToArray();
            UyaIsoSetupTests.AddDiscIdentity(physicalIso);
            BinaryPrimitives.WriteInt32LittleEndian(
                physicalIso.AsSpan(0x500 * UyaLevelConstants.SectorSize + 8), 3);
            using var physicalStream = new MemoryStream(physicalIso, writable: false);
            var physicalSourceLevel = UyaLooseLevelWadExtractor.ExtractPrimary(physicalStream, 3).Bytes;
            var cleanIso = Path.Combine(root, "clean.iso");
            var developmentIso = Path.Combine(root, "development.iso");
            await File.WriteAllBytesAsync(cleanIso, physicalIso);
            await File.WriteAllBytesAsync(developmentIso, physicalIso);
            var cleanHash = Convert.ToHexString(SHA256.HashData(physicalIso));
            var phases = new List<UyaBuildPatchPhase>();
            var buildRequest = new UyaBuildPatchRequest(
                project, catalog.RootPath, cleanIso, developmentIso, new string('a', 32), new HashSet<string>());
            var built = await UyaBuildPatchService.RunAsync(
                buildRequest, "host-test", "sdk-test",
                progress: value =>
                {
                    phases.Add(value.Phase);
                    return ValueTask.CompletedTask;
                });
            Equal(true, built.Succeeded, "one-click build succeeds: " + string.Join(" | ", built.Diagnostics));
            Equal(developmentIso, built.DevelopmentIsoPath, "one-click build identifies development ISO");
            if (built.PatchMode == UyaIsoPatchMode.FullImageReplacement)
                Equal(true, built.NextAction.Contains("savestates retain", StringComparison.Ordinal),
                    "replacement build savestate guidance");
            Equal(true, phases.Contains(UyaBuildPatchPhase.Bake), "one-click build reports bake progress");
            Equal(true, phases.Contains(UyaBuildPatchPhase.Pack), "one-click build reports pack progress");
            Equal(true, phases.Contains(UyaBuildPatchPhase.Plan), "one-click build reports plan progress");
            Equal(true, phases.Contains(UyaBuildPatchPhase.Patch), "one-click build reports patch progress");
            Equal(true, phases.Contains(UyaBuildPatchPhase.Complete), "one-click build reports completion");
            Equal(cleanHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(cleanIso))),
                "one-click build preserves clean ISO");
            await using (var installedIso = File.OpenRead(developmentIso))
            {
                var installedLevel = UyaLooseLevelWadExtractor.ExtractPrimary(installedIso, 3).Bytes;
                Equal(built.OutputLevelWadSha256,
                    Convert.ToHexString(SHA256.HashData(installedLevel)).ToLowerInvariant(),
                    "one-click build installs its verified WAD");
                var installedAssets = ReadAssets(installedLevel);
                foreach (var (kind, marker) in new[]
                    {
                        (AssetKind.Moby, (byte)0x11),
                        (AssetKind.Tie, (byte)0x44),
                        (AssetKind.Shrub, (byte)0x33),
                    })
                    Equal(true, UyaTextureQualification.MatchesBaseColors(
                        installedAssets.Source, installedAssets.Header, installedAssets.AssetWad, kind, marker),
                        $"installed {kind} texture preserves every color");
            }

            var noOpBuild = await UyaBuildPatchService.RunAsync(
                buildRequest, "host-test", "sdk-test");
            Equal(true, noOpBuild.Succeeded, "repeated one-click build succeeds");
            Equal(true, noOpBuild.BakeWasCurrent, "repeated one-click build skips clean layers");
            Equal(0, noOpBuild.BakedLayerCount, "repeated one-click build bakes no layers");
            Equal(built.OutputLevelWadSha256, noOpBuild.OutputLevelWadSha256,
                "repeated one-click build is byte deterministic");

            var interruptedPack = await UyaLevelPackService.PackAsync(
                project,
                catalog,
                physicalSourceLevel,
                new(workspace.Manifest.Target, "ratchet-sdk:sdk-test", "forge-host:host-test"));
            Equal(true, interruptedPack.Succeeded, "interrupted patch fixture packs");
            var interruptedPlan = await UyaIsoPatchService.PlanAsync(
                cleanIso, developmentIso, 3, interruptedPack.OutputBytes!, expectedIsoSize: physicalIso.Length);
            await ThrowsAsync<InvalidOperationException>(() => UyaIsoPatchService.ApplyAsync(
                interruptedPlan,
                progress: null,
                fault: value =>
                {
                    if (value.Phase == UyaIsoPatchFaultPhase.RangeDurable)
                        throw new InvalidOperationException("Injected interrupted patch.");
                },
                CancellationToken.None));
            phases.Clear();
            var recoveredBuild = await UyaBuildPatchService.RunAsync(
                buildRequest, "host-test", "sdk-test",
                progress: value =>
                {
                    phases.Add(value.Phase);
                    return ValueTask.CompletedTask;
                });
            Equal(true, recoveredBuild.Succeeded, "one-click build recovers an interrupted patch");
            Equal(true, phases.Contains(UyaBuildPatchPhase.Recovery), "one-click build reports recovery progress");
            Equal<UyaIsoPatchRecovery?>(null, await UyaIsoPatchService.InspectRecoveryAsync(developmentIso),
                "one-click build clears the interrupted patch journal");

            workspace = await ForgeProjectWorkspace.OpenAsync(project);
            shrub = workspace.Content.Entities.Single(value => value.Layer == "shrubs");
            workspace.UpdateTransform(shrub.EntityId, shrub.Transform with
            {
                Position = shrub.Transform.Position with { Y = shrub.Transform.Position.Y + 1 },
            });
            await workspace.SaveAsync();
            var buildPlan = await UyaBuildPatchService.PlanAsync(
                project, catalog.RootPath, "host-test", "sdk-test");
            Equal(BakeLayerState.Dirty,
                buildPlan.Layers.Single(value => value.Layer == BakeLayerId.Shrubs).State,
                "build plan reports the edited shrub layer");
            Equal(BakeLayerState.DependencyInvalidated,
                buildPlan.Layers.Single(value => value.Layer == BakeLayerId.Lighting).State,
                "build plan reports dependent lighting");
            Equal(true, buildPlan.Layers.Single(value => value.Layer == BakeLayerId.Shrubs).CanDefer,
                "previously staged layers can be deferred");
            var developmentHash = SHA256.HashData(await File.ReadAllBytesAsync(developmentIso));
            using var buildCancellation = new CancellationTokenSource();
            var cancellationError = await ThrowsAsync<OperationCanceledException>(() => UyaBuildPatchService.RunAsync(
                buildRequest,
                "host-test",
                "sdk-test",
                progress: value =>
                {
                    if (value.Phase == UyaBuildPatchPhase.Pack) buildCancellation.Cancel();
                    return ValueTask.CompletedTask;
                },
                cancellationToken: buildCancellation.Token));
            Equal(true, cancellationError.Message.Contains("development ISO was not changed", StringComparison.Ordinal),
                "cancelled one-click build reports resulting state");
            var cancelledDevelopment = await File.ReadAllBytesAsync(developmentIso);
            Equal(true, developmentHash.SequenceEqual(SHA256.HashData(cancelledDevelopment)),
                "cancelled one-click build preserves development ISO");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<AssetCatalogEntry> PutAsync(
        AssetCatalogStore catalog,
        AssetKind kind,
        int classId,
        byte modelByte,
        string level = "level03") =>
        await catalog.PutAsync(kind, UyaAssetImportService.CanonicalFormatVersion, CanonicalAsset(kind, modelByte), new(
            "test",
            new("UYA", "NTSC-U", "1.00", level, "level_wad/assets/asset_wad.bin", 0, new string('a', 32)),
            [$"{kind.ToString().ToLowerInvariant()}:{classId}", $"{kind.ToString().ToLowerInvariant()}:0x{classId:X4}"],
            ["vanilla", "game:UYA", $"level:{level[5..]}"]));

    private static PackedAssets ReadAssets(byte[] levelWad)
    {
        var source = UyaLevelWadRenderPackageBuilder.ReadAssetSourceFiles(
            UyaLevelWadUnpacker.Unpack(levelWad).Files);
        var assetWad = BinaryMagic.IsWad(source.AssetWadBytes)
            ? WadCompression.Decompress(source.AssetWadBytes)
            : source.AssetWadBytes;
        var header = DlAssetReader.ReadHeader(source.HeaderBytes);
        return new(
            source,
            header,
            assetWad,
            DlAssetReader.ReadModelDefinitions(source.HeaderBytes, header.MobyModelOffset, header.MobyModelCount),
            DlAssetReader.ReadModelDefinitions(source.HeaderBytes, header.TieModelOffset, header.TieModelCount),
            DlAssetReader.ReadShrubDefinitions(source.HeaderBytes, header.ShrubModelOffset, header.ShrubModelCount));
    }

    private static byte[] CanonicalAsset(AssetKind kind, byte modelByte)
    {
        var palette = new byte[0x400];
        new byte[] { modelByte, 0, 0, 128, 0, modelByte, 0, 64 }.CopyTo(palette, 0);
        var pif = PifWriter.Write(PifWriter.CreateIndexed8(2, 2, palette, [0, 1, 1, 0]));
        var definitionLength = kind == AssetKind.Shrub ? 0x30 : 0x20;
        var bytes = new byte[24 + definitionLength + pif.Length];
        "HFUYA1"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(6), definitionLength);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10 + definitionLength), 1);
        bytes[14 + definitionLength] = modelByte;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(15 + definitionLength), 1);
        bytes[19 + definitionLength] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20 + definitionLength), pif.Length);
        pif.CopyTo(bytes.AsSpan(24 + definitionLength));
        return bytes;
    }

    private static byte[] ManifestBytes(BakeManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest);

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed record PackedAssets(
        UyaLevelAssetSourceFiles Source,
        DlAssetHeader Header,
        byte[] AssetWad,
        IReadOnlyList<DlAssetModelDefinition> Mobys,
        IReadOnlyList<DlAssetModelDefinition> Ties,
        IReadOnlyList<DlAssetShrubDefinition> Shrubs);
}

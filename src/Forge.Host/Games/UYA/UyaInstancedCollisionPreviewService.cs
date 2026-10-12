using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Ties;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

public static class UyaInstancedCollisionPreviewService
{
    public static async Task<int> CountProxyFacesAsync(
        ForgeProjectWorkspace workspace,
        string catalogRootPath,
        AssetId proxyAssetId,
        CancellationToken cancellationToken)
    {
        if (workspace.Manifest.Target is not { Game: "UYA", Region: "NTSC-U", Revision: "1.00" })
            throw new NotSupportedException("Instanced collision face painting currently supports UYA NTSC-U 1.00 only.");
        var catalog = await AssetCatalogStore.OpenAsync(catalogRootPath, cancellationToken);
        var bytes = await UyaInstancedCollisionCompositionService.ReadAssetAsync(
            workspace, catalog, proxyAssetId, AssetKind.Collision, cancellationToken);
        return CollisionWork.DecodeSolidAddition(
            bytes, GameId.UYA, proxyAssetId.ToString(), cancellationToken).Faces.Count;
    }

    public static async Task<IReadOnlyList<EditorInstancedCollisionCandidate>> GenerateAsync(
        ForgeProjectWorkspace workspace,
        string catalogRootPath,
        EntityId sourceEntityId,
        AssetId sourceAssetId,
        EditorInstancedCollisionGenerationSettings? settings,
        CancellationToken cancellationToken)
    {
        var (catalog, sourceBytes, sourceKind) = await ReadSourceAsync(
            workspace, catalogRootPath, sourceAssetId, cancellationToken);
        var rawType = settings?.RawType ?? 0x0f;
        var profileSections = settings?.ProfileSections ?? 6;
        var surfaceLodIndex = settings?.SurfaceLodIndex ?? -1;
        var targetFaceRatio = settings?.TargetFaceRatio ?? 1f;
        if (targetFaceRatio < 1 && sourceKind != AssetKind.Tfrag)
            throw new InvalidDataException("Surface decimation currently supports tfrag sources only.");

        var candidates = new List<EditorInstancedCollisionCandidate>();
        cancellationToken.ThrowIfCancellationRequested();
        if (settings?.UseHull == true)
        {
            var hull = sourceKind switch
            {
                AssetKind.Tie => CollisionWork.GenerateTieConvexHullCandidate(
                    sourceBytes, GameId.UYA, "preview:hull", rawType: rawType,
                    profileSections: profileSections, cancellationToken: cancellationToken),
                AssetKind.Shrub => CollisionWork.GenerateShrubConvexHullCandidate(
                    sourceBytes, GameId.UYA, "preview:hull", rawType: rawType,
                    profileSections: profileSections, cancellationToken: cancellationToken),
                AssetKind.Tfrag => CollisionWork.GenerateTfragConvexHullCandidate(
                    sourceBytes, GameId.UYA, "preview:hull",
                    lodIndex: TfragLods(sourceBytes, cancellationToken)[0], rawType: rawType,
                    profileSections: profileSections, cancellationToken: cancellationToken),
                _ => throw new InvalidOperationException("Unsupported instanced collision source kind."),
            };
            if (hull.Analysis.HardViolationCount == 0)
                candidates.Add(CreateHull(hull, cancellationToken));
        }
        else
        {
            var decimated = sourceKind switch
            {
                AssetKind.Shrub => CollisionWork.GenerateShrubSurfaceCandidate(
                    sourceBytes, GameId.UYA, "preview:decimated", rawType: rawType,
                    cancellationToken: cancellationToken),
                AssetKind.Tie when surfaceLodIndex < 0 => CollisionWork.GenerateTieDecimatedCandidate(
                    sourceBytes, GameId.UYA, "preview:decimated", rawType: rawType,
                    cancellationToken: cancellationToken),
                AssetKind.Tie => CollisionWork.GenerateTieSurfaceCandidate(
                    sourceBytes, GameId.UYA, "preview:decimated", lodIndex: surfaceLodIndex,
                    rawType: rawType, cancellationToken: cancellationToken),
                AssetKind.Tfrag when targetFaceRatio < 1 => CollisionWork.GenerateTfragDecimatedCandidate(
                    sourceBytes, GameId.UYA, "preview:decimated", rawType: rawType,
                    targetFaceRatio: targetFaceRatio,
                    cancellationToken: cancellationToken),
                AssetKind.Tfrag when surfaceLodIndex < 0 => CollisionWork.GenerateTfragSurfaceCandidate(
                    sourceBytes, GameId.UYA, "preview:surface",
                    lodIndex: TfragLods(sourceBytes, cancellationToken)[0], rawType: rawType,
                    cancellationToken: cancellationToken),
                AssetKind.Tfrag => CollisionWork.GenerateTfragSurfaceCandidate(
                    sourceBytes, GameId.UYA, "preview:decimated", lodIndex: surfaceLodIndex,
                    rawType: rawType, cancellationToken: cancellationToken),
                _ => throw new InvalidOperationException("Unsupported instanced collision source kind."),
            };
            if (decimated.Analysis.HardViolationCount == 0)
                candidates.Add(CreateSurface(decimated, cancellationToken));
        }
        if (candidates.Count == 0)
            throw new InvalidDataException("No generated instanced collision candidate fits the native collision limits.");
        var sourceEntity = workspace.Content.Entities.Single(value => value.EntityId == sourceEntityId);
        var sourcePayloadIndex = UyaInstancedCollisionCompositionService.SourcePayloadIndex(sourceEntity);
        var collisionSource = await UyaInstancedCollisionCompositionService.ReadAsync(
            workspace, catalog, sourcePayloadIndex, cancellationToken);
        var decodedProxies = new Dictionary<AssetId, CollisionSolidAddition>();
        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates[index] = candidates[index] with
            {
                CombinedAnalysis = await AnalyzeCombinedAsync(
                    workspace, catalog, sourceEntityId, sourceAssetId,
                    sourcePayloadIndex, candidates[index], collisionSource, decodedProxies, cancellationToken),
            };
        }
        return candidates;
    }

    public static async Task<EditorInstancedCollisionSourceInfo> InspectAsync(
        ForgeProjectWorkspace workspace,
        string catalogRootPath,
        AssetId sourceAssetId,
        CancellationToken cancellationToken)
    {
        var (_, sourceBytes, sourceKind) = await ReadSourceAsync(
            workspace, catalogRootPath, sourceAssetId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (sourceKind == AssetKind.Shrub) return new(sourceAssetId, [0]);
        if (sourceKind == AssetKind.Tfrag)
            return new(sourceAssetId, TfragLods(sourceBytes, cancellationToken));
        var tie = TieClassReader.Read(
            sourceBytes,
            TieClassReadOptions.ForGameProfile(TieGameProfile.ForGame(GameId.UYA)));
        var lods = tie.LodTopologies
            .Where(value => value.LodIndex is >= 0 and <= 2
                && value.TriangleCount > 0
                && value.UnresolvedLogicalVertexCount == 0)
            .Select(value => value.LodIndex)
            .Distinct()
            .Order()
            .ToArray();
        return new(sourceAssetId, lods);
    }

    private static async Task<(AssetCatalogStore Catalog, byte[] SourceBytes, AssetKind SourceKind)> ReadSourceAsync(
        ForgeProjectWorkspace workspace,
        string catalogRootPath,
        AssetId sourceAssetId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Manifest.Target is not { Game: "UYA", Region: "NTSC-U", Revision: "1.00" })
            throw new NotSupportedException("Instanced collision generation currently supports UYA NTSC-U 1.00 only.");

        var catalog = await AssetCatalogStore.OpenAsync(catalogRootPath, cancellationToken);
        var sourceKinds = workspace.Content.Entities
            .Where(entity => entity.Asset is { } asset && asset.Id == sourceAssetId)
            .Select(entity => entity.Asset!.Kind)
            .Distinct()
            .ToArray();
        if (sourceKinds.Length != 1 || sourceKinds[0] is not (AssetKind.Tie or AssetKind.Shrub or AssetKind.Tfrag))
            throw new InvalidDataException("The selected asset is not a supported canonical UYA collision source.");
        var sourceKind = sourceKinds[0];
        var resolved = workspace.ResolveAsset(new(sourceAssetId, sourceKind), catalog)
            ?? throw new FileNotFoundException($"Source asset {sourceAssetId} is missing.");
        var expectedVersion = sourceKind == AssetKind.Tfrag
            ? ProjectTfragSchema.CanonicalFormatVersion
            : UyaAssetImportService.CanonicalFormatVersion;
        if (resolved.CanonicalFormatVersion != expectedVersion)
            throw new InvalidDataException($"The selected {sourceKind} asset uses an unsupported canonical version.");
        var canonicalBytes = await UyaInstancedCollisionCompositionService.ReadAssetAsync(
            workspace, catalog, sourceAssetId, sourceKind, cancellationToken);
        var sourceBytes = sourceKind == AssetKind.Tfrag
            ? canonicalBytes
            : UyaCanonicalAssetCodec.Decode(canonicalBytes).ModelBytes;
        return (catalog, sourceBytes, sourceKind);
    }

    private static int[] TfragLods(byte[] sourceBytes, CancellationToken cancellationToken)
    {
        var lods = CollisionWork.InspectTfragSurfaceLodIndices(sourceBytes, GameId.UYA, cancellationToken).ToArray();
        return lods.Length > 0
            ? lods
            : throw new InvalidDataException("Tfrag piece contains no usable decoded surface LOD.");
    }

    public static async Task<AssetPreviewResult> PrepareRenderAsync(
        string cacheRootPath,
        string catalogRootPath,
        string sdkRevision,
        AssetId assetId,
        byte[] canonicalBytes,
        CancellationToken cancellationToken)
    {
        var request = new AssetPreviewRequest(
            cacheRootPath,
            catalogRootPath,
            assetId,
            AssetKind.Collision,
            "UYA",
            "collision-default");
        var export = await Task.Run(() => CollisionConverter.ExportGltf(
            canonicalBytes,
            GameId.UYA,
            "model.gltf",
            "model.buffer.bin",
            minify: true), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var package = PackedFilePackageBuilder.Pack(
        [
            new("model.gltf", export.GltfBytes, "model/gltf+json"),
            new("model.buffer.bin", export.BinBytes, "application/octet-stream"),
        ]);
        return await UyaAssetPreviewService.MaterializeAsync(
            request, sdkRevision, package, cancellationToken);
    }

    private static EditorInstancedCollisionCandidate CreateSurface(
        InstancedCollisionCandidate value,
        CancellationToken cancellationToken)
    {
        var encoded = CollisionWork.EncodeStandalone(GameId.UYA, [value.Addition], cancellationToken);
        return Create(
            EditorInstancedCollisionPreset.Surface,
            value.Recipe.TargetFaceRatio < 1
                ? $"Decimated to {value.Recipe.TargetFaceRatio:P0} target · LOD {value.Recipe.LodIndex}"
                : $"Matched LOD {value.Recipe.LodIndex}",
            new(
                ProjectInstancedCollisionRecipeKind.Surface,
                value.Recipe.Version,
                RecipeVersion: 1,
                value.Recipe.LodIndex,
                value.Recipe.RawType,
                TargetFaceRatio: value.Recipe.TargetFaceRatio),
            encoded.Bytes,
            value.GeneratedVertexCount,
            value.GeneratedFaceCount,
            value.MaximumVertexDeviation,
            value.SourceVertexCount,
            value.Analysis);
    }

    private static EditorInstancedCollisionCandidate CreateHull(
        InstancedCollisionCandidate value,
        CancellationToken cancellationToken)
    {
        var encoded = CollisionWork.EncodeStandalone(GameId.UYA, [value.Addition], cancellationToken);
        return Create(
            EditorInstancedCollisionPreset.SolidHull,
            $"Shrinkwrap · {value.Recipe.ProfileSections} sections",
            new(
                ProjectInstancedCollisionRecipeKind.Hull,
                value.Recipe.Version,
                RecipeVersion: 1,
                value.Recipe.LodIndex,
                value.Recipe.RawType,
                ProfileSections: value.Recipe.ProfileSections),
            encoded.Bytes,
            value.GeneratedVertexCount,
            value.GeneratedFaceCount,
            value.MaximumVertexDeviation,
            value.SourceVertexCount,
            value.Analysis);
    }

    private static EditorInstancedCollisionCandidate Create(
        EditorInstancedCollisionPreset preset,
        string label,
        ProjectInstancedCollisionRecipe recipe,
        byte[] bytes,
        int vertexCount,
        int faceCount,
        float maximumDeviation,
        int deviationSampleCount,
        CollisionAnalysis analysis) => new(
            preset,
            label,
            recipe,
            bytes,
            vertexCount,
            faceCount,
            analysis.OccupiedOctantCount,
            analysis.DuplicateFaceCount,
            analysis.HardViolationCount,
            maximumDeviation,
            deviationSampleCount,
            analysis.Octants.Select(octant => new EditorCollisionOctantCost(
                octant.X,
                octant.Y,
                octant.Z,
                octant.FaceCount,
                octant.VertexCount,
                octant.QuadCount,
                octant.EncodedByteCount,
                octant.Violations)).ToArray());

    private static async Task<EditorInstancedCollisionCombinedAnalysis> AnalyzeCombinedAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        EntityId previewEntityId,
        AssetId previewSourceAssetId,
        int sourcePayloadIndex,
        EditorInstancedCollisionCandidate candidate,
        UyaInstancedCollisionCompositionService.CollisionSource collisionSource,
        Dictionary<AssetId, CollisionSolidAddition> decodedProxies,
        CancellationToken cancellationToken)
    {
        var instanceCount = 0;
        try
        {
            var additions = await UyaInstancedCollisionCompositionService.BuildAdditionsAsync(
                workspace,
                catalog,
                sourcePayloadIndex,
                previewEntityId,
                previewSourceAssetId,
                candidate.CanonicalBytes,
                decodedProxies,
                cancellationToken);
            instanceCount = additions.Count;
            var analysis = CollisionWork.AnalyzeCompleteComposition(
                collisionSource.Bytes, GameId.UYA, collisionSource.Edits,
                collisionSource.Additions.Concat(additions).ToArray(), cancellationToken);
            return new(
                instanceCount,
                analysis.LogicalFaceCount,
                analysis.OccupiedOctantCount,
                analysis.DuplicateFaceCount,
                analysis.HardViolationCount,
                analysis.Octants.Select(ToEditorOctant).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException
            or IOException or OverflowException or NotSupportedException)
        {
            return new(instanceCount, 0, 0, 0, 1, [], exception.Message);
        }
    }

    private static EditorCollisionOctantCost ToEditorOctant(CollisionOctantCost octant) => new(
        octant.X,
        octant.Y,
        octant.Z,
        octant.FaceCount,
        octant.VertexCount,
        octant.QuadCount,
        octant.EncodedByteCount,
        octant.Violations,
        octant.AdditionIds);
}

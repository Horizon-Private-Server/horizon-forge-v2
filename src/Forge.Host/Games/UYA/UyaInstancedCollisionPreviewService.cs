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

        var candidates = new List<EditorInstancedCollisionCandidate>();
        cancellationToken.ThrowIfCancellationRequested();
        if (settings?.UseHull == true)
        {
            var hull = sourceKind == AssetKind.Tie
                ? CollisionWork.GenerateTieConvexHullCandidate(
                    sourceBytes, GameId.UYA, "preview:hull", rawType: rawType,
                    profileSections: profileSections, cancellationToken: cancellationToken)
                : CollisionWork.GenerateShrubConvexHullCandidate(
                    sourceBytes, GameId.UYA, "preview:hull", rawType: rawType,
                    profileSections: profileSections, cancellationToken: cancellationToken);
            if (hull.Analysis.HardViolationCount == 0)
                candidates.Add(CreateHull(hull, cancellationToken));
        }
        else
        {
            var decimated = sourceKind == AssetKind.Shrub
                ? CollisionWork.GenerateShrubSurfaceCandidate(
                    sourceBytes, GameId.UYA, "preview:decimated", rawType: rawType,
                    cancellationToken: cancellationToken)
                : surfaceLodIndex < 0
                    ? CollisionWork.GenerateTieDecimatedCandidate(
                        sourceBytes, GameId.UYA, "preview:decimated", rawType: rawType,
                        cancellationToken: cancellationToken)
                    : CollisionWork.GenerateTieSurfaceCandidate(
                        sourceBytes, GameId.UYA, "preview:decimated", lodIndex: surfaceLodIndex,
                        rawType: rawType, cancellationToken: cancellationToken);
            if (decimated.Analysis.HardViolationCount == 0)
                candidates.Add(CreateSurface(decimated, cancellationToken));
        }
        if (candidates.Count == 0)
            throw new InvalidDataException("No generated instanced collision candidate fits the native collision limits.");
        var primary = await UyaInstancedCollisionCompositionService.ReadPrimaryAsync(
            workspace, catalog, sourceEntityId, cancellationToken);
        var decodedProxies = new Dictionary<AssetId, CollisionSolidAddition>();
        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates[index] = candidates[index] with
            {
                CombinedAnalysis = await AnalyzeCombinedAsync(
                    workspace, catalog, sourceEntityId, sourceAssetId,
                    candidates[index], primary, decodedProxies, cancellationToken),
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
        var entry = catalog.Query(new(Id: sourceAssetId)).SingleOrDefault()
            ?? throw new FileNotFoundException($"Source asset {sourceAssetId} is not present in the catalog.");
        if (entry.Kind is not (AssetKind.Tie or AssetKind.Shrub)
            || entry.CanonicalFormatVersion != UyaAssetImportService.CanonicalFormatVersion)
            throw new InvalidDataException("The selected asset is not a supported canonical UYA TIE or shrub.");
        var path = workspace.ResolveAssetPath(sourceAssetId, catalog)
            ?? throw new FileNotFoundException($"Source asset blob {sourceAssetId} is missing.");
        var canonicalBytes = await AssetCatalogBlobReader.ReadVerifiedAsync(
            entry, path, UyaAssetLimits.MaxCanonicalBytes, cancellationToken);
        return (catalog, UyaCanonicalAssetCodec.Decode(canonicalBytes).ModelBytes, entry.Kind);
    }

    public static async Task<UyaAssetPreviewResult> PrepareRenderAsync(
        string cacheRootPath,
        string catalogRootPath,
        string sdkRevision,
        AssetId assetId,
        byte[] canonicalBytes,
        CancellationToken cancellationToken)
    {
        var request = new UyaAssetPreviewRequest(
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
            $"Decimated mesh · LOD {value.Recipe.LodIndex}",
            new(
                ProjectInstancedCollisionRecipeKind.Surface,
                value.Recipe.Version,
                RecipeVersion: 1,
                value.Recipe.LodIndex,
                value.Recipe.RawType),
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
        EditorInstancedCollisionCandidate candidate,
        UyaInstancedCollisionCompositionService.CollisionSource primary,
        Dictionary<AssetId, CollisionSolidAddition> decodedProxies,
        CancellationToken cancellationToken)
    {
        var instanceCount = 0;
        try
        {
            var additions = await UyaInstancedCollisionCompositionService.BuildAdditionsAsync(
                workspace,
                catalog,
                previewEntityId,
                previewSourceAssetId,
                candidate.CanonicalBytes,
                decodedProxies,
                cancellationToken);
            instanceCount = additions.Count;
            var analysis = CollisionWork.AnalyzeComposition(
                primary.Bytes, GameId.UYA, primary.Edits,
                primary.Additions.Concat(additions).ToArray(), cancellationToken);
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

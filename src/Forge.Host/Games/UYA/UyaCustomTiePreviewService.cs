using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaCustomTiePreviewService
{
    private const string DetailsPath = "preview-details.json";
    private const string DiagnosticPath = "diagnostic-target.glb";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<PackedFilePackage> BuildAsync(
        ForgeProjectWorkspace workspace,
        AssetId assetId,
        bool showSource,
        bool includeDetails,
        CancellationToken cancellationToken)
    {
        var model = workspace.Content.CustomModels.Models.SingleOrDefault(value => value.Canonical.Id == assetId)
            ?? throw new InvalidDataException($"Custom model {assetId} is not present in the active project.");
        var classId = workspace.Content.CustomClasses.Allocations
            .Single(value => value.Asset == model.Canonical).ClassId;
        var targetPackage = await BuildTargetAsync(workspace, model, cancellationToken);
        if (!includeDetails && !showSource) return targetPackage;
        var details = CreateDetails(model, classId);
        var detailsBytes = JsonSerializer.SerializeToUtf8Bytes(details, JsonOptions);
        var diagnostic = BuildDiagnosticGlb(targetPackage, details);
        cancellationToken.ThrowIfCancellationRequested();
        if (!showSource)
            return AddFiles(targetPackage,
                new(DetailsPath, detailsBytes, "application/json"),
                new(DiagnosticPath, diagnostic, "model/gltf-binary"));

        var files = new List<PackedFile>
        {
            new("model.gltf", await workspace.ReadAttachedAssetVerifiedAsync(
                model.Source, ForgeProjectWorkspace.MaxAttachedAssetBytes, cancellationToken),
                model.SourceName.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)
                    ? "model/gltf-binary" : "model/gltf+json"),
        };
        foreach (var dependency in model.Dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Add(new(
                UyaRenderPackageService.NormalizeEntryPath(dependency.Uri),
                await workspace.ReadAttachedAssetVerifiedAsync(
                    dependency.Asset, ForgeProjectWorkspace.MaxAttachedAssetBytes, cancellationToken),
                dependency.Kind == ProjectCustomModelDependencyKind.Image
                    ? ImageContentType(dependency.Uri) : "application/octet-stream"));
        }
        files.Add(new(DetailsPath, detailsBytes, "application/json"));
        files.Add(new(DiagnosticPath, diagnostic, "model/gltf-binary"));
        return PackedFilePackageBuilder.Pack(files);
    }

    internal static async Task<PackedFilePackage> BuildTargetAsync(
        ForgeProjectWorkspace workspace,
        ProjectCustomModel model,
        CancellationToken cancellationToken)
    {
        var envelope = UyaCustomTieNativeCodec.Read(await workspace.ReadAttachedAssetVerifiedAsync(
            model.Native, ForgeProjectWorkspace.MaxAttachedAssetBytes, cancellationToken));
        var textures = await UyaCustomTieTextureReader.ReadMaterialTexturesAsync(
            workspace, model, cancellationToken);
        return await Task.Run(() => FrontendAssetPackageBuilder.Build(
            GameId.UYA,
            FrontendAssetKind.Tie,
            envelope.ModelBytes,
            textures,
            new(PreserveAuthoredRepeatedTexCoordTiles: true)), cancellationToken);
    }

    private static PackedFilePackage AddFiles(PackedFilePackage package, params PackedFile[] additions)
    {
        var files = package.Entries.Select(entry => new PackedFile(
            entry.Path,
            package.PackedBytes.AsSpan(entry.Offset, entry.Length).ToArray(),
            entry.ContentType)).Concat(additions).ToArray();
        return PackedFilePackageBuilder.Pack(files);
    }

    private static CustomModelPreviewDetails CreateDetails(ProjectCustomModel model, int classId) => new(
        1,
        false,
        model.SourceName,
        classId,
        model.Metadata.VertexCount,
        model.Metadata.TriangleCount,
        model.Metadata.PacketCount,
        model.Metadata.MaterialCount,
        model.Metadata.TextureCount,
        model.Metadata.PaletteCount,
        model.Metadata.Bounds,
        model.Metadata.LodPolicy,
        model.Metadata.Primitives,
        model.Metadata.Diagnostics);

    private static byte[] BuildDiagnosticGlb(
        PackedFilePackage package,
        CustomModelPreviewDetails details)
    {
        var files = package.Entries.ToDictionary(
            entry => entry.Path,
            entry => package.PackedBytes.AsSpan(entry.Offset, entry.Length).ToArray(),
            StringComparer.Ordinal);
        var root = JsonNode.Parse(files["model.gltf"])?.AsObject()
            ?? throw new InvalidDataException("Target preview glTF is invalid.");
        var buffers = root["buffers"]?.AsArray()
            ?? throw new InvalidDataException("Target preview glTF has no buffers.");
        if (buffers.Count != 1 || buffers[0] is not JsonObject buffer)
            throw new InvalidDataException("Target preview glTF must have one buffer.");
        var bufferUri = buffer["uri"]?.GetValue<string>()
            ?? throw new InvalidDataException("Target preview glTF buffer has no URI.");
        if (!files.TryGetValue(bufferUri, out var modelBytes))
            throw new InvalidDataException("Target preview glTF buffer is missing.");

        using var binary = new MemoryStream();
        binary.Write(modelBytes);
        Pad(binary, 0);
        var views = root["bufferViews"]?.AsArray() ?? new JsonArray();
        root["bufferViews"] = views;
        if (root["images"] is JsonArray images)
        {
            foreach (var imageNode in images)
            {
                if (imageNode is not JsonObject image || image["uri"] is not JsonValue uriValue) continue;
                var uri = uriValue.GetValue<string>();
                if (!files.TryGetValue(uri, out var imageBytes))
                    throw new InvalidDataException($"Target preview image {uri} is missing.");
                var offset = checked((int)binary.Position);
                binary.Write(imageBytes);
                Pad(binary, 0);
                var viewIndex = views.Count;
                views.Add(new JsonObject
                {
                    ["buffer"] = 0,
                    ["byteOffset"] = offset,
                    ["byteLength"] = imageBytes.Length,
                });
                image.Remove("uri");
                image["bufferView"] = viewIndex;
                image["mimeType"] = ImageContentType(uri);
            }
        }
        buffer.Remove("uri");
        buffer["byteLength"] = checked((int)binary.Length);
        var asset = root["asset"]?.AsObject()
            ?? throw new InvalidDataException("Target preview glTF has no asset metadata.");
        asset["extras"] = new JsonObject
        {
            ["horizonForgeDiagnostic"] = JsonSerializer.SerializeToNode(details, JsonOptions),
            ["notice"] = "Debug inspection artifact only; native project assets remain authoritative.",
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(root, JsonOptions);
        var jsonLength = Align4(json.Length);
        var binaryBytes = binary.ToArray();
        var totalLength = checked(12 + 8 + jsonLength + 8 + binaryBytes.Length);
        var output = new byte[totalLength];
        BinaryPrimitives.WriteUInt32LittleEndian(output, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), totalLength);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(12), jsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16), 0x4E4F534A);
        json.CopyTo(output.AsSpan(20));
        output.AsSpan(20 + json.Length, jsonLength - json.Length).Fill(0x20);
        var binHeader = 20 + jsonLength;
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(binHeader), binaryBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(binHeader + 4), 0x004E4942);
        binaryBytes.CopyTo(output.AsSpan(binHeader + 8));
        return output;
    }

    private static void Pad(Stream stream, byte value)
    {
        while ((stream.Position & 3) != 0) stream.WriteByte(value);
    }

    private static int Align4(int value) => checked((value + 3) & ~3);

    private static string ImageContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "image/png",
    };

    private sealed record CustomModelPreviewDetails(
        int SchemaVersion,
        bool Authoritative,
        string SourceName,
        int TargetClassId,
        int VertexCount,
        int TriangleCount,
        int PacketCount,
        int MaterialCount,
        int TextureCount,
        int PaletteCount,
        ProjectCustomModelBounds Bounds,
        string LodPolicy,
        IReadOnlyList<ProjectCustomModelPrimitiveBinding> Primitives,
        IReadOnlyList<string> Diagnostics);
}

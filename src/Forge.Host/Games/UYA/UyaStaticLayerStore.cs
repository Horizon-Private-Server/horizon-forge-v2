using Forge.Host.Domain;
using System.Security.Cryptography;
using System.Text.Json;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.Games.UYA;

public static class UyaStaticLayerStore
{
    private const long MaxAssetBytes = 256L * 1024 * 1024;

    public static async Task WriteSourceAsync(
        string projectRoot,
        OpaqueContentSource source,
        IReadOnlyList<UyaStaticLayerSourceRecord> layers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(layers);
        var manifest = new UyaStaticLayerSourceManifest(
            UyaStaticLayerSchema.CurrentVersion,
            UyaStaticLayerSchema.SourceDocumentType,
            source with { Fingerprint = source.Fingerprint.ToLowerInvariant() },
            layers);
        ValidateSource(manifest, null);
        await ForgeProjectPersistence.WriteFileSafelyAsync(
            ForgeProjectPersistence.ResolveRelativePath(projectRoot, UyaStaticLayerSchema.RelativeSourcePath),
            ForgeProjectPersistence.Serialize(manifest),
            cancellationToken);
    }

    public static async Task<UyaStaticLayerInspection> InspectAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        var blockers = UyaStaticLayerSchema.Layers.ToDictionary(layer => layer, _ => new List<string>());
        var path = ForgeProjectPersistence.ResolveRelativePath(projectRoot, UyaStaticLayerSchema.RelativeSourcePath);
        if (!File.Exists(path)) return Missing(blockers, "Static-instance source metadata is missing");
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Static-instance source metadata cannot be a symbolic link.");
            var manifest = ForgeProjectPersistence.Deserialize<UyaStaticLayerSourceManifest>(
                await File.ReadAllBytesAsync(path, cancellationToken), "UYA static-instance source");
            var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot, cancellationToken);
            ValidateSource(manifest, workspace);
            return new(manifest, blockers.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException)
        {
            return Missing(blockers, $"Static-instance source metadata is invalid ({exception.Message})");
        }
    }

    public static async Task<IReadOnlyList<BakeLayerInput>> CreateBakeInputsAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        CancellationToken cancellationToken = default)
    {
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot, cancellationToken);
        var inspection = await InspectAsync(projectRoot, cancellationToken);
        var result = new List<BakeLayerInput>(UyaStaticLayerSchema.Layers.Count);
        foreach (var layer in UyaStaticLayerSchema.Layers)
        {
            var prepared = await PrepareAsync(workspace, catalog, inspection, layer, cancellationToken);
            result.Add(new(layer, prepared.AuthoritativeContent, prepared.AssetIds,
                ReadOnlyMemory<byte>.Empty, prepared.Blockers));
        }
        return result;
    }

    public static async Task<BakeLayerSnapshot> StageAsync(
        string projectRoot,
        AssetCatalogStore catalog,
        BakeStagingStore staging,
        BakeLayerPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (!UyaStaticLayerSchema.Layers.Contains(plan.Layer))
            throw new ArgumentException("Plan is not a UYA instance layer.", nameof(plan));
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot, cancellationToken);
        var inspection = await InspectAsync(projectRoot, cancellationToken);
        var prepared = await PrepareAsync(workspace, catalog, inspection, plan.Layer, cancellationToken);
        if (prepared.Blockers.Count > 0) throw new InvalidDataException(string.Join(' ', prepared.Blockers));

        return await staging.CommitAsync(plan, async (output, token) =>
        {
            await ForgeProjectPersistence.WriteFileSafelyAsync(
                Path.Combine(output, "manifest.json"), prepared.AuthoritativeContent.ToArray(), token);
            await ForgeProjectPersistence.WriteFileSafelyAsync(
                Path.Combine(output, "instances.bin"), prepared.InstanceBytes, token);
            var resourceRoot = Path.Combine(output, "resources");
            Directory.CreateDirectory(resourceRoot);
            foreach (var definition in prepared.Manifest!.Definitions)
            {
                var source = workspace.ResolveAssetPath(definition.Asset.Id, catalog)
                    ?? throw new FileNotFoundException($"Static asset {definition.Asset.Id} disappeared during bake.");
                await CopyAsync(source, Path.Combine(output, definition.Resource), token);
            }
        }, (output, token) => ValidateOutputAsync(output, prepared, plan.Layer, token), cancellationToken);
    }

    private static async Task ValidateOutputAsync(
        string output,
        PreparedLayer prepared,
        BakeLayerId layer,
        CancellationToken cancellationToken)
    {
        var manifest = prepared.Manifest!;
        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(output, "manifest.json"), cancellationToken);
        if (!manifestBytes.SequenceEqual(prepared.AuthoritativeContent.Span))
            throw new InvalidDataException($"{layer} staged manifest changed during write.");
        var instances = await File.ReadAllBytesAsync(Path.Combine(output, "instances.bin"), cancellationToken);
        if (instances.Length != manifest.InstancesSize || Hash(instances) != manifest.InstancesSha256)
            throw new InvalidDataException($"{layer} staged instances failed size or checksum validation.");
        var classes = layer switch
        {
            BakeLayerId.Ties => UyaTieInstancesReader.Read(instances).Instances.Select(value => value.ClassId).ToArray(),
            BakeLayerId.Shrubs => UyaShrubInstancesReader.Read(instances).Instances.Select(value => value.ClassId).ToArray(),
            BakeLayerId.Mobys => UyaMobyInstancesReader.Read(instances).Instances.Select(value => value.ClassId).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(layer)),
        };
        if (classes.Length != manifest.Instances.Count)
            throw new InvalidDataException($"{layer} staged instance count does not match its manifest.");
        var definitions = manifest.Definitions.ToDictionary(value => value.TargetIndex);
        for (var index = 0; index < manifest.Instances.Count; index++)
        {
            var instance = manifest.Instances[index];
            if (instance.TargetIndex != index
                || classes[index] != instance.ClassId)
                throw new InvalidDataException($"{layer} staged instance {index} has an invalid definition reference.");
            if (instance.DefinitionIndex == -1 && layer == BakeLayerId.Mobys) continue;
            if (!definitions.TryGetValue(instance.DefinitionIndex, out var definition)
                || definition.ClassId != instance.ClassId)
                throw new InvalidDataException($"{layer} staged instance {index} has an invalid definition reference.");
        }
        foreach (var definition in manifest.Definitions)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(output, definition.Resource), cancellationToken);
            if (bytes.LongLength is <= 0 or > MaxAssetBytes
                || !ValidCanonicalAsset(bytes, definition.Asset.Kind)
                || AssetId.Compute(definition.Asset.Kind, definition.CanonicalFormatVersion, bytes) != definition.Asset.Id)
                throw new InvalidDataException($"{layer} staged definition {definition.TargetIndex} failed asset validation.");
        }
    }

    private static async Task<PreparedLayer> PrepareAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        UyaStaticLayerInspection inspection,
        BakeLayerId layer,
        CancellationToken cancellationToken)
    {
        var blockers = inspection.Blockers[layer].ToList();
        var source = inspection.Manifest?.Layers.SingleOrDefault(value => value.Layer == layer);
        var (kind, layerName) = layer switch
        {
            BakeLayerId.Ties => (AssetKind.Tie, "ties"),
            BakeLayerId.Shrubs => (AssetKind.Shrub, "shrubs"),
            BakeLayerId.Mobys => (AssetKind.Moby, "mobys"),
            _ => throw new ArgumentOutOfRangeException(nameof(layer)),
        };
        var entities = workspace.Content.Entities
            .Where(entity => entity.Layer == layerName && entity.State?.Disabled != true)
            .OrderBy(entity => entity.Provenance?.SourceIndex ?? int.MaxValue)
            .ThenBy(entity => entity.EntityId.ToString(), StringComparer.Ordinal)
            .ToArray();
        var resolved = new List<ResolvedEntity>(entities.Length);
        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entity.Source is null || source is null || entity.Source.RawRecord.Length != source.RecordSize)
            {
                blockers.Add($"{entity.Name} ({entity.EntityId}) has no compatible UYA source record.");
                continue;
            }
            if (layer == BakeLayerId.Mobys && !Uniform(entity.Transform.Scale))
            {
                blockers.Add($"{entity.Name} ({entity.EntityId}) uses non-uniform scale, which UYA mobys cannot encode.");
                continue;
            }
            if (entity.Asset is null)
            {
                if (layer == BakeLayerId.Mobys && entity.Source.ModelLess)
                {
                    resolved.Add(new(entity, null, entity.Source.ClassId));
                    continue;
                }
                blockers.Add($"{entity.Name} ({entity.EntityId}) has no {kind} asset reference.");
                continue;
            }
            if (entity.Asset.Kind != kind)
            {
                blockers.Add($"{entity.Name} ({entity.EntityId}) references {entity.Asset.Kind}, expected {kind}.");
                continue;
            }
            var entry = catalog.Query(new(Id: entity.Asset.Id)).SingleOrDefault();
            var path = workspace.ResolveAssetPath(entity.Asset.Id, catalog);
            if (entry is null || path is null)
            {
                blockers.Add($"{entity.Name} ({entity.EntityId}) references missing vanilla asset {entity.Asset.Id}.");
                continue;
            }
            if (entry.Kind != kind || entry.CanonicalFormatVersion != UyaStaticLayerSchema.CanonicalFormatVersion
                || !entry.Tags.Contains("vanilla", StringComparer.Ordinal)
                || !entry.Sources.Any(value => value.Game == "UYA" && value.Region == "NTSC-U"))
            {
                blockers.Add($"{entity.Name} ({entity.EntityId}) asset {entity.Asset.Id} is not compatible vanilla UYA data.");
                continue;
            }
            var classId = ResolveClassId(entry, entity.Source.ClassId, kind);
            if (classId is null)
            {
                blockers.Add($"{entity.Name} ({entity.EntityId}) asset {entity.Asset.Id} has ambiguous class identity.");
                continue;
            }
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaxAssetBytes || info.Length != entry.Size)
            {
                blockers.Add($"{entity.Name} ({entity.EntityId}) asset {entity.Asset.Id} has invalid size.");
                continue;
            }
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (AssetId.Compute(kind, entry.CanonicalFormatVersion, bytes) != entry.Id
                || !ValidCanonicalAsset(bytes, kind))
            {
                blockers.Add($"{entity.Name} ({entity.EntityId}) asset {entity.Asset.Id} has invalid canonical data.");
                continue;
            }
            resolved.Add(new(entity, entry, classId.Value));
        }

        foreach (var conflict in resolved.Where(value => value.Entry is not null).GroupBy(value => value.ClassId)
            .Where(group => group.Select(value => value.Entry!.Id).Distinct().Count() > 1))
            blockers.Add($"{kind} class 0x{conflict.Key:X4} resolves to multiple assets.");
        if (blockers.Count > 0 || source is null)
            return new(null, [], ForgeProjectPersistence.Serialize(new { layer }), [], blockers);

        var definitions = resolved.Where(value => value.Entry is not null)
            .Select(value => (value.ClassId, Entry: value.Entry!))
            .DistinctBy(value => (value.ClassId, value.Entry.Id))
            .OrderBy(value => value.ClassId)
            .ThenBy(value => value.Entry.Id.ToString(), StringComparer.Ordinal)
            .Select((value, index) => new UyaStaticBakeDefinition(
                index,
                value.ClassId,
                new(value.Entry.Id, kind),
                value.Entry.CanonicalFormatVersion,
                $"resources/{index:D4}-{value.Entry.Id}.hfuya"))
            .ToArray();
        var definitionIndexes = definitions.ToDictionary(value => (value.ClassId, value.Asset.Id), value => value.TargetIndex);
        var instances = resolved.Select((value, index) => new UyaStaticBakeInstance(
            index,
            value.Entity.EntityId,
            value.Entry is null ? -1 : definitionIndexes[(value.ClassId, value.Entry.Id)],
            value.ClassId)).ToArray();
        var instanceBytes = layer switch
        {
            BakeLayerId.Ties => UyaTieInstancesWriter.Write(
                new(0, source.HeaderWords, [], source.TrailingBytes),
                resolved.Select(value => ToStaticEdit(value.Entity, value.ClassId)).ToArray()),
            BakeLayerId.Shrubs => UyaShrubInstancesWriter.Write(
                new(0, source.HeaderWords, [], source.TrailingBytes),
                resolved.Select(value => ToStaticEdit(value.Entity, value.ClassId)).ToArray()),
            BakeLayerId.Mobys => UyaMobyInstancesWriter.Write(
                new(0, source.HeaderWords[0], source.HeaderWords[1], source.HeaderWords[2], [], source.TrailingBytes),
                resolved.Select(value => ToMobyEdit(value.Entity, value.ClassId)).ToArray()),
            _ => throw new ArgumentOutOfRangeException(nameof(layer)),
        };
        var manifest = new UyaStaticBakeManifest(
            UyaStaticLayerSchema.CurrentVersion,
            UyaStaticLayerSchema.BakeDocumentType,
            layer,
            UyaStaticLayerSchema.Encoding,
            instanceBytes.Length,
            Hash(instanceBytes),
            definitions,
            instances);
        return new(manifest, instanceBytes, ForgeProjectPersistence.Serialize(manifest),
            definitions.Select(value => value.Asset.Id).ToArray(), []);
    }

    private static UyaStaticInstanceEdit ToStaticEdit(ProjectEntity entity, int classId) => new(
        classId,
        new(entity.Transform.Position.X, entity.Transform.Position.Y, entity.Transform.Position.Z),
        new(entity.Transform.Rotation.X, entity.Transform.Rotation.Y,
            entity.Transform.Rotation.Z, entity.Transform.Rotation.W),
        new(entity.Transform.Scale.X, entity.Transform.Scale.Y, entity.Transform.Scale.Z),
        entity.Source!.RawRecord);

    private static UyaMobyInstanceEdit ToMobyEdit(ProjectEntity entity, int classId) => new(
        classId,
        new(entity.Transform.Position.X, entity.Transform.Position.Y, entity.Transform.Position.Z),
        new(entity.Transform.Rotation.X, entity.Transform.Rotation.Y,
            entity.Transform.Rotation.Z, entity.Transform.Rotation.W),
        entity.Transform.Scale.X,
        entity.Source!.RawRecord);

    private static bool Uniform(ProjectVector3 scale) =>
        MathF.Abs(scale.X - scale.Y) < 0.0001f && MathF.Abs(scale.X - scale.Z) < 0.0001f;

    private static int? ResolveClassId(AssetCatalogEntry entry, int sourceClassId, AssetKind kind)
    {
        var prefix = $"{kind.ToString().ToLowerInvariant()}:";
        var ids = entry.Aliases.Where(value => value.StartsWith(prefix, StringComparison.Ordinal)
                && !value.StartsWith(prefix + "0x", StringComparison.Ordinal))
            .Select(value => int.TryParse(value.AsSpan(prefix.Length), out var id) ? id : -1)
            .Where(value => value >= 0).Distinct().Order().ToArray();
        if (ids.Contains(sourceClassId)) return sourceClassId;
        return ids.Length == 1 ? ids[0] : null;
    }

    private static bool ValidCanonicalAsset(ReadOnlySpan<byte> bytes, AssetKind kind)
    {
        try
        {
            var asset = UyaCanonicalAssetCodec.Decode(bytes);
            return asset.DefinitionBytes.Length == (kind == AssetKind.Shrub ? 0x30 : 0x20);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static void ValidateSource(UyaStaticLayerSourceManifest manifest, ForgeProjectWorkspace? workspace)
    {
        if (manifest.SchemaVersion != UyaStaticLayerSchema.CurrentVersion
            || manifest.DocumentType != UyaStaticLayerSchema.SourceDocumentType
            || manifest.Source is null
            || manifest.Layers is null
            || manifest.Layers.Count != UyaStaticLayerSchema.Layers.Count
            || !UyaStaticLayerSchema.Layers.All(layer => manifest.Layers.Count(value => value?.Layer == layer) == 1))
            throw new InvalidDataException("Static-instance source schema is invalid.");
        if (new[] { manifest.Source.Game, manifest.Source.Region, manifest.Source.Revision, manifest.Source.Fingerprint }
                .Any(string.IsNullOrWhiteSpace)
            || manifest.Source.Game != "UYA"
            || manifest.Source.Region != "NTSC-U"
            || manifest.Source.Level < 0
            || manifest.Source.Fingerprint.Length != 32
            || manifest.Source.Fingerprint.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("Static-instance source identity is invalid.");
        foreach (var layer in manifest.Layers)
        {
            var expectedSize = layer.Layer switch
            {
                BakeLayerId.Ties => UyaTieInstancesReader.RecordSize,
                BakeLayerId.Shrubs => UyaShrubInstancesReader.RecordSize,
                BakeLayerId.Mobys => UyaMobyInstancesReader.RecordSize,
                _ => -1,
            };
            if (!UyaStaticLayerSchema.Layers.Contains(layer.Layer) || layer.RecordSize != expectedSize
                || layer.HeaderWords is null || layer.HeaderWords.Count != 3 || layer.TrailingBytes is null
                || layer.TrailingBytes.LongLength > MaxAssetBytes)
                throw new InvalidDataException($"Static-instance {layer.Layer} source metadata is invalid.");
        }
        if (workspace is not null && (manifest.Source.Game != workspace.Manifest.BaseLevel.Game
            || manifest.Source.Region != workspace.Manifest.BaseLevel.Region
            || manifest.Source.Revision != workspace.Manifest.BaseLevel.Revision
            || manifest.Source.Level != workspace.Manifest.BaseLevel.Level
            || !manifest.Source.Fingerprint.Equals(
                workspace.Manifest.BaseLevel.SourceFingerprint, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Static-instance source does not match this project.");
    }

    private static UyaStaticLayerInspection Missing(
        Dictionary<BakeLayerId, List<string>> blockers,
        string message)
    {
        foreach (var values in blockers.Values) values.Add(message + "; migrate it from the verified source ISO.");
        return new(null, blockers.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value));
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task CopyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    private sealed record ResolvedEntity(ProjectEntity Entity, AssetCatalogEntry? Entry, int ClassId);

    private sealed record PreparedLayer(
        UyaStaticBakeManifest? Manifest,
        byte[] InstanceBytes,
        ReadOnlyMemory<byte> AuthoritativeContent,
        IReadOnlyList<AssetId> AssetIds,
        IReadOnlyList<string> Blockers);
}

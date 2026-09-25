using Forge.Host.Domain;
using System.Security.Cryptography;
using System.Text.Json;
using RatchetPs2.Core.Gameplay;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.Games.UYA;

public static class UyaGameplayLayerStore
{
    private const int MaxSectionBytes = 256 * 1024 * 1024;

    public static Task WriteSourceAsync(
        string projectRoot,
        OpaqueContentSource source,
        GameplayPvarTables? tables,
        CancellationToken cancellationToken = default) => WriteSourceAsync(
            projectRoot,
            source,
            tables is null
                ? []
                :
                [
                    (Name: "pvar_moby_links", Bytes: tables.MobyLinksBytes),
                    (Name: "pvar_table", Bytes: tables.TableBytes),
                    (Name: "pvar_data", Bytes: tables.DataBytes),
                    (Name: "pvar_relative_pointers", Bytes: tables.RelativePointerBytes),
                ],
            cancellationToken);

    public static Task WriteSourceAsync(
        string projectRoot,
        OpaqueContentSource source,
        UyaGameplayBlocks gameplay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gameplay);
        return WriteSourceAsync(
            projectRoot,
            source,
            gameplay.Blocks.Where(value => UyaGameplayLayerSchema.SectionNames.Contains(value.SemanticName))
                .Select(value => (value.SemanticName, value.PayloadBytes)).ToArray(),
            cancellationToken);
    }

    private static async Task WriteSourceAsync(
        string projectRoot,
        OpaqueContentSource source,
        IReadOnlyList<(string Name, byte[] Bytes)> values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var root = ForgeProjectPersistence.ResolveRelativePath(projectRoot, UyaGameplayLayerSchema.RelativeRootPath);
        var blobs = Path.Combine(root, "blobs");
        RejectLink(root);
        RejectLink(blobs);
        Directory.CreateDirectory(blobs);
        var sections = new List<UyaGameplaySourceSection>();
        foreach (var value in values.Where(value => value.Bytes.Length > 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value.Bytes.Length > MaxSectionBytes) throw new InvalidDataException($"{value.Name} exceeds the gameplay section limit.");
            var hash = Hash(value.Bytes);
            var blob = $"blobs/{value.Name}-{hash}.bin";
            await ForgeProjectPersistence.WriteFileSafelyAsync(
                ForgeProjectPersistence.ResolveRelativePath(root, blob), value.Bytes, cancellationToken);
            sections.Add(new(value.Name, value.Bytes.Length, hash, blob));
        }
        var manifest = new UyaGameplaySourceManifest(
            UyaGameplayLayerSchema.CurrentVersion,
            UyaGameplayLayerSchema.SourceDocumentType,
            source with { Fingerprint = source.Fingerprint.ToLowerInvariant() },
            sections.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray());
        ValidateManifest(manifest, null);
        await ForgeProjectPersistence.WriteFileSafelyAsync(
            Path.Combine(root, UyaGameplayLayerSchema.ManifestFileName),
            ForgeProjectPersistence.Serialize(manifest),
            cancellationToken);
    }

    public static async Task<UyaGameplayInspection> InspectAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        var root = ForgeProjectPersistence.ResolveRelativePath(projectRoot, UyaGameplayLayerSchema.RelativeRootPath);
        var path = Path.Combine(root, UyaGameplayLayerSchema.ManifestFileName);
        if (!File.Exists(path)) return Missing("Gameplay source metadata is missing");
        try
        {
            RejectLink(root);
            RejectLink(Path.Combine(root, "blobs"));
            RejectLink(path);
            var manifest = ForgeProjectPersistence.Deserialize<UyaGameplaySourceManifest>(
                await File.ReadAllBytesAsync(path, cancellationToken), "UYA gameplay source");
            var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot, cancellationToken);
            ValidateManifest(manifest, workspace);
            var blockers = new List<string>();
            foreach (var section in manifest.Sections)
            {
                var blob = ForgeProjectPersistence.ResolveRelativePath(root, section.Blob);
                if (!File.Exists(blob))
                {
                    blockers.Add($"Gameplay section {section.Name} is missing; migrate it from the verified source ISO.");
                    continue;
                }
                RejectLink(blob);
                if (new FileInfo(blob).Length != section.Size
                    || Hash(await File.ReadAllBytesAsync(blob, cancellationToken)) != section.Sha256)
                    blockers.Add($"Gameplay section {section.Name} failed integrity validation; migrate it from the verified source ISO.");
            }
            return new(manifest, blockers);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException)
        {
            return Missing($"Gameplay source metadata is invalid ({exception.Message})");
        }
    }

    public static async Task<BakeLayerInput> CreateBakeInputAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(projectRoot, cancellationToken);
        return new(BakeLayerId.Gameplay, prepared.ManifestBytes, [], ReadOnlyMemory<byte>.Empty, prepared.Blockers);
    }

    public static async Task<BakeLayerSnapshot> StageAsync(
        string projectRoot,
        BakeStagingStore staging,
        BakeLayerPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (plan.Layer != BakeLayerId.Gameplay)
            throw new ArgumentException("Gameplay staging requires the gameplay layer plan.", nameof(plan));
        var prepared = await PrepareAsync(projectRoot, cancellationToken);
        if (prepared.Blockers.Count > 0) throw new InvalidDataException(string.Join(' ', prepared.Blockers));
        return await staging.CommitAsync(plan, async (output, token) =>
        {
            await ForgeProjectPersistence.WriteFileSafelyAsync(
                Path.Combine(output, "manifest.json"), prepared.ManifestBytes.ToArray(), token);
            foreach (var section in prepared.Manifest!.Sections)
                await ForgeProjectPersistence.WriteFileSafelyAsync(
                    ForgeProjectPersistence.ResolveRelativePath(output, section.Path),
                    prepared.Bytes[section.Name],
                    token);
        }, (output, token) => ValidateOutputAsync(output, prepared, token), cancellationToken);
    }

    private static async Task ValidateOutputAsync(
        string output,
        PreparedGameplay prepared,
        CancellationToken cancellationToken)
    {
        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(output, "manifest.json"), cancellationToken);
        if (!manifestBytes.SequenceEqual(prepared.ManifestBytes.Span))
            throw new InvalidDataException("Gameplay staged manifest changed during write.");
        var blocks = new List<GameplayRawBlock>();
        foreach (var section in prepared.Manifest!.Sections)
        {
            var bytes = await File.ReadAllBytesAsync(
                ForgeProjectPersistence.ResolveRelativePath(output, section.Path), cancellationToken);
            if (bytes.Length != section.Size || Hash(bytes) != section.Sha256)
                throw new InvalidDataException($"Gameplay staged section {section.Name} failed size or checksum validation.");
            blocks.Add(new(blocks.Count, 0x58 + blocks.Count * 4, 0, section.Name, bytes));
        }
        var tables = GameplayPvarTableReader.Read(blocks, "UYA");
        for (var index = 0; index < prepared.Manifest.Mobys.Count; index++)
        {
            var moby = prepared.Manifest.Mobys[index];
            if (moby.TargetIndex != index
                || moby.PvarIndex < -1
                || (moby.PvarIndex >= 0 && (tables is null || moby.PvarIndex >= tables.Entries.Count)))
                throw new InvalidDataException($"Gameplay staged moby reference {index} is invalid.");
        }
    }

    private static async Task<PreparedGameplay> PrepareAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var workspace = await ForgeProjectWorkspace.OpenAsync(projectRoot, cancellationToken);
        var inspection = await InspectAsync(projectRoot, cancellationToken);
        var blockers = inspection.Blockers.ToList();
        var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (inspection.IsValid)
        {
            var root = ForgeProjectPersistence.ResolveRelativePath(projectRoot, UyaGameplayLayerSchema.RelativeRootPath);
            foreach (var section in inspection.Manifest!.Sections)
                bytes[section.Name] = await File.ReadAllBytesAsync(
                    ForgeProjectPersistence.ResolveRelativePath(root, section.Blob), cancellationToken);
        }

        GameplayPvarTables? tables = null;
        if (blockers.Count == 0)
        {
            try
            {
                tables = GameplayPvarTableReader.Read(UyaGameplayLayerSchema.SectionNames.Select((name, index) =>
                    new GameplayRawBlock(index, 0x58 + index * 4, 0, name, bytes.GetValueOrDefault(name) ?? [])).ToArray(), "UYA");
            }
            catch (InvalidDataException exception)
            {
                blockers.Add(exception.Message);
            }
        }

        var enabled = OrderedMobys(workspace).Where(entity => entity.State?.Disabled != true).ToArray();
        var disabled = OrderedMobys(workspace).Where(entity => entity.State?.Disabled == true).ToArray();
        var references = new List<UyaGameplayMobyReference>(enabled.Length);
        for (var index = 0; index < enabled.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entity = enabled[index];
            if (entity.Source?.RawRecord.Length != UyaMobyInstancesReader.RecordSize)
            {
                blockers.Add($"{entity.Name} ({entity.EntityId}) has no compatible UYA moby record.");
                continue;
            }
            var moby = UyaMobyInstancesReader.ReadInstance(entity.Source.RawRecord);
            if (moby.PvarIndex < -1 || (moby.PvarIndex >= 0 && (tables is null || moby.PvarIndex >= tables.Entries.Count)))
                blockers.Add($"{entity.Name} ({entity.EntityId}) references missing pvar index {moby.PvarIndex}.");
            references.Add(new(index, entity.EntityId, moby.Uid, moby.PvarIndex));
        }
        if (disabled.Length > 0 && bytes.GetValueOrDefault("pvar_moby_links") is { Length: > 0 })
            blockers.Add($"{disabled.Length} disabled moby instance(s) cannot be removed while pvar_moby_links requires index remapping.");
        if (tables is not null)
            foreach (var pointer in tables.RelativePointers.Where(value => value.PvarIndex < -1 || value.PvarIndex >= tables.Entries.Count))
                blockers.Add($"Pvar relative pointer references missing pvar index {pointer.PvarIndex}.");

        var instanceReferences = PatchInstanceSections(workspace, bytes, blockers);

        var sections = bytes.Select(section => new UyaGameplayBakeSection(
            section.Key,
            $"pvars/{section.Key}.bin",
            section.Value.Length,
            Hash(section.Value))).OrderBy(value => value.Name, StringComparer.Ordinal).ToArray();
        var manifest = new UyaGameplayBakeManifest(
            UyaGameplayLayerSchema.CurrentVersion,
            UyaGameplayLayerSchema.BakeDocumentType,
            UyaGameplayLayerSchema.Encoding,
            sections,
            references,
            instanceReferences);
        return new(manifest, ForgeProjectPersistence.Serialize(manifest), bytes, blockers);
    }

    private static IReadOnlyList<UyaGameplayInstanceReference> PatchInstanceSections(
        ForgeProjectWorkspace workspace,
        IDictionary<string, byte[]> bytes,
        ICollection<string> blockers)
    {
        var references = new List<UyaGameplayInstanceReference>();
        foreach (var section in UyaGameplayLayerSchema.WritableInstanceSections)
        {
            var entities = workspace.Content.Entities
                .Where(value => value.Provenance?.Section == $"gameplay/core/{section}")
                .OrderBy(value => value.Provenance!.SourceIndex)
                .ToArray();
            if (!bytes.TryGetValue(section, out var source))
            {
                if (entities.Length > 0) blockers.Add($"Gameplay section {section} is missing; migrate it from the verified source ISO.");
                continue;
            }
            try
            {
                var count = SourceCount(section, source);
                if (!entities.Select(value => value.Provenance!.SourceIndex).SequenceEqual(Enumerable.Range(0, count)))
                {
                    blockers.Add($"Gameplay section {section} has unsupported inserted, deleted, or reordered instances.");
                    continue;
                }
                references.AddRange(entities.Select(value => new UyaGameplayInstanceReference(
                    section, value.Provenance!.SourceIndex, value.EntityId, value.Transform)));
                if (section == "splines")
                {
                    var sourceSplines = GameplayGeometryReader.ReadSplines(source);
                    var changedSplines = entities.Where(value => !Equivalent(value.Transform, ProjectTransform.Identity)
                        || !Equivalent(value.Geometry!.Spline!.Points,
                            sourceSplines[value.Provenance!.SourceIndex].Points)).ToArray();
                    if (changedSplines.Length > 0) bytes[section] = WriteSplines(source, changedSplines);
                    continue;
                }
                var changed = entities.Where(value => !Equivalent(value.Transform, SourceTransform(section, source, value)))
                    .ToArray();
                if (changed.Any(value => Shape(value)?.CameraCollision is not null))
                {
                    blockers.Add($"Gameplay section {section} contains a moved camera-collision shape; grid regeneration is not available.");
                    continue;
                }
                if (changed.Length == 0) continue;
                bytes[section] = WriteTransforms(section, source, changed);
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or OverflowException)
            {
                blockers.Add($"Gameplay section {section} cannot be serialized ({exception.Message}).");
            }
        }
        return references;
    }

    private static int SourceCount(string section, byte[] source) => section switch
    {
        "cameras" => UyaCameraInstancesReader.Read(source).Count,
        "sound_instances" => UyaSoundInstancesReader.Read(source).Count,
        "cuboids" => GameplayGeometryReader.ReadCuboids(source).Length,
        "spheres" => GameplayGeometryReader.ReadShapes(source, "sphere").Length,
        "cylinders" => GameplayGeometryReader.ReadShapes(source, "cylinder").Length,
        "pills" => GameplayGeometryReader.ReadShapes(source, "pill").Length,
        "splines" => GameplayGeometryReader.ReadSplines(source).Length,
        _ => throw new ArgumentOutOfRangeException(nameof(section)),
    };

    private static ProjectTransform SourceTransform(string section, byte[] source, ProjectEntity entity)
    {
        var index = entity.Provenance!.SourceIndex;
        return section switch
        {
            "cameras" => CameraTransform(UyaCameraInstancesReader.Read(source).Instances[index]),
            "sound_instances" => UyaBaseLevelService.Decompose(
                UyaSoundInstancesReader.Read(source).Instances[index].Matrix)
                ?? throw new InvalidDataException($"Sound {index} source transform cannot be decomposed."),
            "splines" => ProjectTransform.Identity,
            _ => UyaBaseLevelService.Decompose(ShapeMatrix(section, source, index))
                ?? throw new InvalidDataException($"Shape {index} source transform cannot be decomposed."),
        };
    }

    private static IReadOnlyList<float> ShapeMatrix(string section, byte[] source, int index) => section switch
    {
        "cuboids" => GameplayGeometryReader.ReadCuboids(source)[index].Matrix,
        "spheres" => GameplayGeometryReader.ReadShapes(source, "sphere")[index].Matrix,
        "cylinders" => GameplayGeometryReader.ReadShapes(source, "cylinder")[index].Matrix,
        "pills" => GameplayGeometryReader.ReadShapes(source, "pill")[index].Matrix,
        _ => throw new ArgumentOutOfRangeException(nameof(section)),
    };

    private static ProjectTransform CameraTransform(UyaCameraInstance value) => new(
        new(value.Position.X, value.Position.Y, value.Position.Z),
        UyaBaseLevelService.FromZyxEuler(new(value.Rotation.X, value.Rotation.Y, value.Rotation.Z)),
        new(1, 1, 1));

    private static ProjectShapeGeometry? Shape(ProjectEntity entity) => entity.Geometry is { } geometry
        ? geometry.Cuboid ?? geometry.Sphere ?? geometry.Cylinder ?? geometry.Pill
        : null;

    private static byte[] WriteTransforms(string section, byte[] source, IReadOnlyList<ProjectEntity> entities)
    {
        var edits = entities.Select(value => new UyaInstanceTransformEdit(
            value.Provenance!.SourceIndex,
            Position(value.Transform.Position),
            Rotation(value.Transform.Rotation),
            Position(value.Transform.Scale))).ToArray();
        return section switch
        {
            "cameras" => UyaCameraInstancesWriter.Write(source, entities.Select(value => new UyaCameraInstanceEdit(
                value.Provenance!.SourceIndex, Position(value.Transform.Position), Rotation(value.Transform.Rotation))).ToArray()),
            "sound_instances" => UyaSoundInstancesWriter.Write(source, edits),
            "cuboids" => UyaShapeInstancesWriter.WriteCuboids(source, edits),
            "spheres" => UyaShapeInstancesWriter.WriteSpheres(source, edits),
            "cylinders" => UyaShapeInstancesWriter.WriteCylinders(source, edits),
            "pills" => UyaShapeInstancesWriter.WritePills(source, edits),
            _ => throw new ArgumentOutOfRangeException(nameof(section)),
        };
    }

    private static byte[] WriteSplines(byte[] source, IReadOnlyList<ProjectEntity> entities) =>
        UyaSplineInstancesWriter.Write(source, entities.Select(value => new UyaSplineInstanceEdit(
            value.Provenance!.SourceIndex,
            value.Geometry!.Spline!.Points.Select(point => new UyaVector4(point.X, point.Y, point.Z, point.W)).ToArray(),
            Position(value.Transform.Position),
            Rotation(value.Transform.Rotation),
            Position(value.Transform.Scale))).ToArray());

    private static bool Equivalent(IReadOnlyList<ProjectVector4> left, IReadOnlyList<GameplayVector4> right) =>
        left.Count == right.Count && left.Select((point, index) =>
            point.X == right[index].X && point.Y == right[index].Y
            && point.Z == right[index].Z && point.W == right[index].W).All(value => value);

    private static UyaVector3 Position(ProjectVector3 value) => new(value.X, value.Y, value.Z);

    private static UyaQuaternion Rotation(ProjectQuaternion value) => new(value.X, value.Y, value.Z, value.W);

    private static bool Equivalent(ProjectTransform left, ProjectTransform right)
    {
        const float epsilon = 0.00001f;
        static bool Near(float left, float right) => MathF.Abs(left - right) <= epsilon;
        var dot = left.Rotation.X * right.Rotation.X + left.Rotation.Y * right.Rotation.Y
            + left.Rotation.Z * right.Rotation.Z + left.Rotation.W * right.Rotation.W;
        var leftLength = MathF.Sqrt(left.Rotation.X * left.Rotation.X + left.Rotation.Y * left.Rotation.Y
            + left.Rotation.Z * left.Rotation.Z + left.Rotation.W * left.Rotation.W);
        var rightLength = MathF.Sqrt(right.Rotation.X * right.Rotation.X + right.Rotation.Y * right.Rotation.Y
            + right.Rotation.Z * right.Rotation.Z + right.Rotation.W * right.Rotation.W);
        return Near(left.Position.X, right.Position.X) && Near(left.Position.Y, right.Position.Y)
            && Near(left.Position.Z, right.Position.Z) && Near(left.Scale.X, right.Scale.X)
            && Near(left.Scale.Y, right.Scale.Y) && Near(left.Scale.Z, right.Scale.Z)
            && MathF.Abs(MathF.Abs(dot / (leftLength * rightLength)) - 1) <= epsilon;
    }

    private static IEnumerable<ProjectEntity> OrderedMobys(ForgeProjectWorkspace workspace) =>
        workspace.Content.Entities.Where(entity => entity.Layer == "mobys")
            .OrderBy(entity => entity.Provenance?.SourceIndex ?? int.MaxValue)
            .ThenBy(entity => entity.EntityId.ToString(), StringComparer.Ordinal);

    private static void ValidateManifest(UyaGameplaySourceManifest manifest, ForgeProjectWorkspace? workspace)
    {
        if (manifest.SchemaVersion != UyaGameplayLayerSchema.CurrentVersion
            || manifest.DocumentType != UyaGameplayLayerSchema.SourceDocumentType
            || manifest.Source is null || manifest.Sections is null)
            throw new InvalidDataException("Gameplay source schema is invalid.");
        if (manifest.Source.Game != "UYA" || manifest.Source.Region != "NTSC-U"
            || string.IsNullOrWhiteSpace(manifest.Source.Revision)
            || manifest.Source.Level < 0
            || manifest.Source.Fingerprint.Length != 32
            || manifest.Source.Fingerprint.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("Gameplay source identity is invalid.");
        if (manifest.Sections.Any(section => section is null
                || !UyaGameplayLayerSchema.SectionNames.Contains(section.Name)
                || section.Size is <= 0 or > MaxSectionBytes
                || !BakeSchema.IsFingerprint(section.Sha256)
                || section.Blob != $"blobs/{section.Name}-{section.Sha256}.bin")
            || manifest.Sections.Select(section => section.Name).Distinct(StringComparer.Ordinal).Count() != manifest.Sections.Count)
            throw new InvalidDataException("Gameplay source sections are invalid.");
        if (workspace is not null && (manifest.Source.Game != workspace.Manifest.BaseLevel.Game
            || manifest.Source.Region != workspace.Manifest.BaseLevel.Region
            || manifest.Source.Revision != workspace.Manifest.BaseLevel.Revision
            || manifest.Source.Level != workspace.Manifest.BaseLevel.Level
            || !manifest.Source.Fingerprint.Equals(
                workspace.Manifest.BaseLevel.SourceFingerprint, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Gameplay source does not match this project.");
    }

    private static UyaGameplayInspection Missing(string message) =>
        new(null, [$"{message}; migrate it from the verified source ISO."]);

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path))
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Gameplay source cannot use symbolic links.");
    }

    private sealed record PreparedGameplay(
        UyaGameplayBakeManifest? Manifest,
        ReadOnlyMemory<byte> ManifestBytes,
        IReadOnlyDictionary<string, byte[]> Bytes,
        IReadOnlyList<string> Blockers);
}

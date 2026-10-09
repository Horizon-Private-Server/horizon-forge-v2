using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Forge.Host.Domain;

internal static class AuthoringFoundationQualificationTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-authoring-qualification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await VerifySchemaMatrixAsync(root);
            await VerifyPortableAuthoringProjectAsync(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifySchemaMatrixAsync(string root)
    {
        var seedPath = Path.Combine(root, "schema-seed");
        var (seed, ids) = await CreateFixtureAsync(seedPath);
        var manifestTemplate = Parse(await File.ReadAllBytesAsync(
            Path.Combine(seedPath, ForgeProjectWorkspace.ManifestFileName)));
        var contentTemplate = Parse(Decompress(await File.ReadAllBytesAsync(seed.ContentFilePath)));

        for (var version = ProjectSchema.OldestSupportedVersion; version <= ProjectSchema.CurrentVersion; version++)
        {
            var fixturePath = Path.Combine(root, $"schema-v{version}");
            var manifest = Clone(manifestTemplate);
            var content = Clone(contentTemplate);
            var contentRelativePath = version < 4 ? "content/project.json" : ForgeProjectWorkspace.DefaultContentPath;
            manifest["schemaVersion"] = version;
            manifest["content"] = contentRelativePath;
            content["schemaVersion"] = version;
            if (version == 0)
            {
                manifest.Remove("documentType");
                content.Remove("documentType");
            }
            if (version <= 8) MakeLegacyCollisionFields(content);
            if (version < 10) content.Remove("assetOverrides");
            if (version < 11) MakeLegacyAreaLinks(content);
            if (version is >= 2 and <= 7)
                manifest["baseLevel"]!.AsObject()["entityVersion"] = 13;

            var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
            var contentBytes = Encoding.UTF8.GetBytes(content.ToJsonString());
            var storedContent = version < 4 ? contentBytes : Compress(contentBytes);
            var contentPath = Path.Combine(fixturePath, contentRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(contentPath)!);
            await File.WriteAllBytesAsync(Path.Combine(fixturePath, ForgeProjectWorkspace.ManifestFileName), manifestBytes);
            await File.WriteAllBytesAsync(contentPath, storedContent);

            var opened = await ForgeProjectWorkspace.OpenAsync(fixturePath);
            Equal(ProjectSchema.CurrentVersion, opened.Manifest.SchemaVersion, $"v{version} manifest migration");
            Equal(ProjectSchema.CurrentVersion, opened.Content.SchemaVersion, $"v{version} content migration");
            Equal(ids.SplineId, Reference(opened, ProjectReferences.AreaSplines), $"v{version} spline reference");
            Equal(ids.CuboidId, Reference(opened, ProjectReferences.AreaCuboids), $"v{version} cuboid reference");
            Equal(ids.TieId, opened.Content.Entities.Single(entity => entity.EntityId == ids.CollisionId)
                .Collision!.Attachment!.ParentEntityId, $"v{version} collision attachment");
            Equal(true, opened.Content.Entities.Single(entity => entity.EntityId == ids.TieId)
                .InstancedCollisionEnabled, $"v{version} collision flag");
            Equal(false, opened.IsDirty, $"v{version} opens cleanly");
            var manifestAfterOpen = await File.ReadAllBytesAsync(
                Path.Combine(fixturePath, ForgeProjectWorkspace.ManifestFileName));
            var contentAfterOpen = await File.ReadAllBytesAsync(contentPath);
            Equal(true, manifestBytes.SequenceEqual(manifestAfterOpen), $"v{version} open preserves manifest");
            Equal(true, storedContent.SequenceEqual(contentAfterOpen), $"v{version} open preserves content");

            await opened.SaveAsync();
            Equal(false, Directory.Exists(Path.Combine(fixturePath, ForgeProjectWorkspace.RecoveryDirectoryName, ".save-journal")),
                $"v{version} journal completes");
            Equal(true, await File.ReadAllBytesAsync(opened.ContentFilePath) is [0x1f, 0x8b, ..],
                $"v{version} saves current compressed content");
            if (contentRelativePath != ForgeProjectWorkspace.DefaultContentPath)
                Equal(false, File.Exists(contentPath), $"v{version} old content path is retired after save");
            var reopened = await ForgeProjectWorkspace.OpenAsync(fixturePath);
            Equal(ids.SplineId, Reference(reopened, ProjectReferences.AreaSplines),
                $"v{version} reference survives journaled upgrade");
        }

        var futurePath = Path.Combine(root, "schema-future");
        var futureManifest = Clone(manifestTemplate);
        var futureContent = Clone(contentTemplate);
        futureManifest["schemaVersion"] = ProjectSchema.CurrentVersion + 1;
        futureContent["schemaVersion"] = ProjectSchema.CurrentVersion + 1;
        var futureManifestBytes = Encoding.UTF8.GetBytes(futureManifest.ToJsonString());
        var futureContentBytes = Compress(Encoding.UTF8.GetBytes(futureContent.ToJsonString()));
        Directory.CreateDirectory(Path.Combine(futurePath, "content"));
        var futureManifestPath = Path.Combine(futurePath, ForgeProjectWorkspace.ManifestFileName);
        var futureContentPath = Path.Combine(futurePath, ForgeProjectWorkspace.DefaultContentPath);
        await File.WriteAllBytesAsync(futureManifestPath, futureManifestBytes);
        await File.WriteAllBytesAsync(futureContentPath, futureContentBytes);
        await ThrowsAsync<UnsupportedProjectSchemaException>(() => ForgeProjectWorkspace.OpenAsync(futurePath));
        var futureManifestAfterOpen = await File.ReadAllBytesAsync(futureManifestPath);
        var futureContentAfterOpen = await File.ReadAllBytesAsync(futureContentPath);
        Equal(true, futureManifestBytes.SequenceEqual(futureManifestAfterOpen), "future manifest is never overwritten");
        Equal(true, futureContentBytes.SequenceEqual(futureContentAfterOpen), "future content is never overwritten");
    }

    private static async Task VerifyPortableAuthoringProjectAsync(string root)
    {
        var catalog = await AssetCatalogStore.OpenAsync(Path.Combine(root, "portable-catalog"));
        var source = await catalog.PutAsync(
            AssetKind.Texture,
            2,
            "vanilla texture"u8.ToArray(),
            new("qualification", new("UYA", "NTSC-U", "1.00", "level03", "hud", 1,
                "ba9f2b38c7346e7b6e5b8e87717d5893")));
        var projectPath = Path.Combine(root, "portable-project");
        var (workspace, ids) = await CreateFixtureAsync(projectPath, new(source.Id, source.Kind));
        var replacementBytes = "synthetic overridden texture"u8.ToArray();
        var replacement = await workspace.AttachAssetAsync(AssetKind.Texture, 2, replacementBytes, source.Id);
        workspace.SetAssetOverride(new(source.Id, source.Kind), new(replacement.Id, replacement.Kind));
        await workspace.SaveAsync();

        var before = workspace.CaptureState();
        workspace.UpdateEntityReference(ids.AreaId, ProjectReferences.AreaCuboids, 2, ids.SecondCuboidId);
        var after = workspace.CaptureState();
        var history = new EditorHistory();
        history.Push(before, after, [], [], [ids.AreaId], 512);
        Equal(true, history.TryUndo(out var state, out _, out _), "typed reference undo is available");
        workspace.RestoreState(state);
        Equal(ids.CuboidId, Reference(workspace, ProjectReferences.AreaCuboids), "typed reference undo restores target");
        Equal(true, history.TryRedo(out state, out _, out _), "typed reference redo is available");
        workspace.RestoreState(state);
        Equal(ids.SecondCuboidId, Reference(workspace, ProjectReferences.AreaCuboids), "typed reference redo restores target");

        var recovery = await workspace.WriteRecoveryAsync()
            ?? throw new InvalidOperationException("Expected authoring qualification recovery snapshot.");
        workspace.UpdateEntityReference(ids.AreaId, ProjectReferences.AreaCuboids, 2, ids.CuboidId);
        await workspace.LoadRecoveryAsync(recovery.Id);
        Equal(ids.SecondCuboidId, Reference(workspace, ProjectReferences.AreaCuboids),
            "typed reference survives recovery");
        Equal(replacement.Id, workspace.ResolveAssetReference(new(source.Id, source.Kind)).Id,
            "override survives recovery");
        await workspace.SaveAsync();
        var savedFingerprint = workspace.CurrentFingerprint;

        var archivePath = Path.Combine(root, "portable-authoring.zip");
        ZipFile.CreateFromDirectory(projectPath, archivePath, CompressionLevel.Fastest, includeBaseDirectory: false);
        var transferredPath = Path.Combine(root, "portable-authoring-transferred");
        ZipFile.ExtractToDirectory(archivePath, transferredPath);
        var transferred = await ForgeProjectWorkspace.OpenAsync(transferredPath);
        Equal(savedFingerprint, transferred.CurrentFingerprint, "zip transfer preserves deterministic project state");
        Equal(ids.SecondCuboidId, Reference(transferred, ProjectReferences.AreaCuboids),
            "cuboid reference survives zip transfer");
        Equal(ids.SplineId, Reference(transferred, ProjectReferences.AreaSplines),
            "path reference survives zip transfer");
        var effective = transferred.ResolveAssetReference(new(source.Id, source.Kind));
        Equal(replacement.Id, effective.Id, "override survives zip transfer");
        var transferredReplacementBytes = await File.ReadAllBytesAsync(
            transferred.ResolveAttachedAssetPath(replacement.Id)
                ?? throw new InvalidOperationException("Transferred override blob did not resolve."));
        Equal(true, replacementBytes.SequenceEqual(transferredReplacementBytes), "override bytes survive zip transfer");

        var graph = ProjectReferenceGraph.Create(transferred.Content);
        var finalOrder = new Dictionary<EntityId, int>
        {
            [ids.SplineId] = 7,
            [ids.SecondCuboidId] = 4,
        };
        var resolved = graph.Outgoing(ids.AreaId)
            .Where(edge => edge.Reference.EntityId is not null)
            .OrderBy(edge => edge.Reference.FieldKey, StringComparer.Ordinal)
            .Select(edge => ProjectReferences.ResolveNativeIndex(edge, finalOrder))
            .ToArray();
        Equal(true, resolved.SequenceEqual(graph.Outgoing(ids.AreaId)
            .Where(edge => edge.Reference.EntityId is not null)
            .OrderBy(edge => edge.Reference.FieldKey, StringComparer.Ordinal)
            .Select(edge => ProjectReferences.ResolveNativeIndex(edge, finalOrder))),
            "typed references resolve deterministically for bake");
        Equal(effective, transferred.ResolveAssetReference(new(source.Id, source.Kind)),
            "asset overrides resolve deterministically for bake");
        Equal(true, (await transferred.ListRecoveriesAsync()).Any(snapshot => snapshot.Id == recovery.Id),
            "zip transfer preserves recovery evidence");
    }

    private static async Task<(ForgeProjectWorkspace Workspace, FixtureIds Ids)> CreateFixtureAsync(
        string path,
        ProjectAssetReference? texture = null)
    {
        var splineId = EntityId.New();
        var cuboidId = EntityId.New();
        var secondCuboidId = EntityId.New();
        var areaId = EntityId.New();
        var tieId = EntityId.New();
        var collisionId = EntityId.New();
        var entities = new List<ProjectEntity>
        {
            new(splineId, "Path", "splines", ProjectTransform.Identity, null,
                Geometry: new(Spline: new([new(0, 0, 0, 0), new(1, 2, 3, 0)]))),
            Cuboid(cuboidId, "Cuboid A"),
            Cuboid(secondCuboidId, "Cuboid B"),
            new(areaId, "Area", "areas", ProjectTransform.Identity, null,
                Geometry: new(Area: new(new(0, 0, 0, 10), 0,
                    [Link(1, ProjectReferences.AreaSplines, ProjectEntityKind.Spline, splineId)],
                    [Link(2, ProjectReferences.AreaCuboids, ProjectEntityKind.Cuboid, cuboidId)], [], [], []))),
            new(tieId, "TIE", "ties", ProjectTransform.Identity,
                new(AssetId.Parse(new string('b', AssetId.TextLength)), AssetKind.Tie),
                InstancedCollisionEnabled: true),
            new(collisionId, "Collision", "collision", ProjectTransform.Identity,
                new(AssetId.Parse(new string('c', AssetId.TextLength)), AssetKind.Collision),
                new("UYA", 3, "level_wad/collision", 0),
                Collision: new(ProjectCollisionPieceKind.Solid, 0, 0, 1, 3, [new(0, 1)],
                    new(tieId, ProjectTransform.Identity))),
        };
        if (texture is not null)
            entities.Add(new(EntityId.New(), "HUD texture", "hud", ProjectTransform.Identity, texture));
        var workspace = await ForgeProjectWorkspace.CreateAsync(
            path,
            "Authoring qualification",
            new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
            new("UYA", "NTSC-U", "1.00", 3, "ba9f2b38c7346e7b6e5b8e87717d5893"),
            entities);
        return (workspace, new(splineId, cuboidId, secondCuboidId, areaId, tieId, collisionId));
    }

    private static ProjectEntity Cuboid(EntityId id, string name) => new(
        id, name, "cuboids", ProjectTransform.Identity, null,
        Geometry: new(Cuboid: new(
            [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
            [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0],
            new(0, 0, 0))));

    private static ProjectGeometryLink Link(
        int sourceIndex,
        string fieldKey,
        ProjectEntityKind kind,
        EntityId target) => new(
        sourceIndex,
        Reference: new(ProjectReferenceDomain.Entity, fieldKey, true, kind, target));

    private static EntityId? Reference(ForgeProjectWorkspace workspace, string fieldKey) =>
        ProjectReferenceGraph.Create(workspace.Content).Outgoing(workspace.Content.Entities
            .Single(entity => entity.Geometry?.Area is not null).EntityId)
            .Single(edge => edge.Reference.FieldKey == fieldKey).Reference.EntityId;

    private static void MakeLegacyCollisionFields(JsonObject content)
    {
        if (content.Remove("instancedCollisionBindings", out var bindings))
            content["tieCollisionBindings"] = bindings;
        if (content["tieCollisionBindings"] is JsonArray legacyBindings)
            foreach (var binding in legacyBindings.OfType<JsonObject>())
                if (binding.Remove("sourceAssetId", out var sourceId)) binding["tieAssetId"] = sourceId;
        foreach (var entity in content["entities"]!.AsArray().OfType<JsonObject>())
        {
            if (entity.Remove("instancedCollisionEnabled", out var enabled)) entity["tieCollisionEnabled"] = enabled;
            if (entity["collision"] is JsonObject collision
                && collision["attachment"] is JsonObject attachment
                && attachment.Remove("parentEntityId", out var parentId))
                attachment["tieEntityId"] = parentId;
        }
    }

    private static void MakeLegacyAreaLinks(JsonObject content)
    {
        foreach (var entity in content["entities"]!.AsArray().OfType<JsonObject>())
        {
            if (entity["geometry"]?["area"] is not JsonObject area) continue;
            foreach (var key in new[] { "splines", "cuboids", "spheres", "cylinders", "negativeCuboids" })
            {
                foreach (var link in area[key]!.AsArray().OfType<JsonObject>())
                {
                    if (link["reference"] is JsonObject reference && reference.Remove("entityId", out var target))
                        link["entityId"] = target;
                    link.Remove("reference");
                }
            }
        }
    }

    private static JsonObject Parse(byte[] bytes) => JsonNode.Parse(bytes)!.AsObject();

    private static JsonObject Clone(JsonObject value) => JsonNode.Parse(value.ToJsonString())!.AsObject();

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(bytes);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    private sealed record FixtureIds(
        EntityId SplineId,
        EntityId CuboidId,
        EntityId SecondCuboidId,
        EntityId AreaId,
        EntityId TieId,
        EntityId CollisionId);
}

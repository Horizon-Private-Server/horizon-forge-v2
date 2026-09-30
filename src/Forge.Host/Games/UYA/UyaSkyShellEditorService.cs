using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Skyboxes;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaSkyShellEditorService
{
    private const float RuntimeFrameRate = 60f;

    public static async Task<IReadOnlyList<EntityId>> ExecuteAsync(
        ForgeProjectWorkspace workspace,
        string catalogRootPath,
        EditorCommand command,
        CancellationToken cancellationToken)
    {
        if (workspace.Manifest.Target is not { Game: "UYA", Region: "NTSC-U" })
            throw new NotSupportedException("Sky shell editing is not supported for this project target.");
        var catalog = await AssetCatalogStore.OpenAsync(catalogRootPath, cancellationToken);

        switch (command.Kind)
        {
            case EditorCommandKind.AddSkyShellFromAsset:
            {
                var source = command.SkyShellSource!;
                var resolved = await ResolveAsync(workspace, catalog, source.AssetId, cancellationToken);
                if ((uint)source.ShellIndex >= (uint)resolved.Skybox.Shells.Count)
                    throw new InvalidDataException(
                        $"Sky shell index {source.ShellIndex} is outside its 0..{resolved.Skybox.Shells.Count - 1} range.");
                var shell = resolved.Skybox.Shells[source.ShellIndex];
                var order = workspace.Content.Entities.Count(entity => entity.SkyShell is not null);
                var entity = new ProjectEntity(
                    EntityId.New(),
                    $"Sky shell {order + 1}",
                    "sky",
                    ProjectTransform.Identity,
                    new(source.AssetId, AssetKind.Sky),
                    new("UYA", resolved.Level, "level_wad/assets/sky", source.ShellIndex),
                    SkyShell: new(
                        source.ShellIndex,
                        order,
                        Radians(shell.RotationX, shell.RotationY, shell.RotationZ, SkyboxFormat.RotationTickRadians),
                        Radians(shell.RotationDeltaX, shell.RotationDeltaY, shell.RotationDeltaZ,
                            SkyboxFormat.RotationTickRadians * RuntimeFrameRate)));
                await ComposeAsync(
                    workspace, catalog, workspace.Content.Entities.Append(entity).ToArray(), cancellationToken);
                workspace.AddEntity(entity);
                return [entity.EntityId];
            }
            case EditorCommandKind.UpdateSkyShell:
            {
                var entity = workspace.Content.Entities.Single(value => value.EntityId == command.EntityIds[0]);
                var current = entity.SkyShell ?? throw new InvalidOperationException("Entity is not a sky shell.");
                var update = command.SkyShellUpdate!;
                var changed = current with
                {
                    InitialRotationRadians = update.InitialRotationRadians is null
                        ? current.InitialRotationRadians
                        : Quantize(update.InitialRotationRadians, SkyboxFormat.RotationTickRadians, "initial rotation"),
                    AngularVelocityRadiansPerSecond = update.AngularVelocityRadiansPerSecond is null
                        ? current.AngularVelocityRadiansPerSecond
                        : Quantize(update.AngularVelocityRadiansPerSecond,
                            SkyboxFormat.RotationTickRadians * RuntimeFrameRate, "angular velocity"),
                };
                await ComposeAsync(
                    workspace,
                    catalog,
                    workspace.Content.Entities.Select(value => value.EntityId == entity.EntityId
                        ? value with { SkyShell = changed }
                        : value).ToArray(),
                    cancellationToken);
                workspace.UpdateSkyShell(entity.EntityId, changed);
                return command.EntityIds;
            }
            case EditorCommandKind.ReorderSkyShell:
            {
                var ordered = workspace.Content.Entities.Where(entity => entity.SkyShell is not null)
                    .OrderBy(entity => entity.SkyShell!.Order).ToList();
                var moved = ordered.SingleOrDefault(entity => entity.EntityId == command.EntityIds[0])
                    ?? throw new InvalidOperationException("Entity is not a sky shell.");
                var destination = command.DestinationOrder!.Value;
                if ((uint)destination >= (uint)ordered.Count)
                    throw new ArgumentOutOfRangeException(nameof(command), "Sky shell destination order is outside the composition.");
                ordered.Remove(moved);
                ordered.Insert(destination, moved);
                var orders = ordered.Select((entity, order) => (entity.EntityId, Order: order))
                    .ToDictionary(value => value.EntityId, value => value.Order);
                await ComposeAsync(
                    workspace,
                    catalog,
                    workspace.Content.Entities.Select(entity => entity.SkyShell is null
                        ? entity
                        : entity with { SkyShell = entity.SkyShell with { Order = orders[entity.EntityId] } }).ToArray(),
                    cancellationToken);
                workspace.ReorderSkyShell(moved.EntityId, destination);
                return command.EntityIds;
            }
            case EditorCommandKind.DeleteEntities:
            case EditorCommandKind.DuplicateEntities:
            case EditorCommandKind.PasteEntities:
            case EditorCommandKind.SetEntityState:
                await ComposeAsync(
                    workspace, catalog, workspace.Content.Entities, cancellationToken);
                return command.EntityIds;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), "Command is not a sky shell edit.");
        }
    }

    internal static Task<SkyboxCompositionResult> ComposeAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        IReadOnlyList<ProjectEntity> entities,
        CancellationToken cancellationToken) =>
        ComposeAsync(workspace, catalog, entities, null, cancellationToken);

    internal static async Task<SkyboxCompositionResult> ComposeAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        IReadOnlyList<ProjectEntity> entities,
        UyaBaseLayerInspection? inspection,
        CancellationToken cancellationToken)
    {
        var shells = entities.Where(entity => entity.SkyShell is not null)
            .OrderBy(entity => entity.SkyShell!.Order).ToArray();
        if (shells.Length > SkyboxFormat.MaxShellCount)
            throw new InvalidDataException($"UYA skyboxes support at most {SkyboxFormat.MaxShellCount} shells.");
        if (!shells.Select(entity => entity.SkyShell!.Order).SequenceEqual(Enumerable.Range(0, shells.Length)))
            throw new InvalidDataException("Sky shell order must be unique and contiguous.");

        inspection ??= await UyaBaseLayerStore.InspectAsync(
            workspace.RootPath, catalog, BakeLayerId.Sky, cancellationToken);
        var blockers = inspection.Blockers[BakeLayerId.Sky];
        if (blockers.Count > 0) throw new InvalidDataException(string.Join(' ', blockers));
        var baseAsset = inspection.Manifest!.Layers.Single(layer => layer.Layer == BakeLayerId.Sky)
            .Assets.Single(asset => asset.Name == "sky.bin").Asset;
        var basePath = workspace.ResolveAssetPath(baseAsset.Id, catalog)
            ?? throw new FileNotFoundException($"Base sky asset {baseAsset.Id} is missing.");
        var baseBytes = await File.ReadAllBytesAsync(basePath, cancellationToken);

        var resolved = new Dictionary<AssetId, ResolvedSky>();
        var composition = new List<SkyboxShellComposition>(shells.Length);
        foreach (var entity in shells.Where(entity => entity.State?.Disabled != true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var assetId = entity.Asset?.Id
                ?? throw new InvalidDataException($"Sky shell {entity.EntityId} has no source asset.");
            if (!resolved.TryGetValue(assetId, out var source))
            {
                source = await ResolveAsync(workspace, catalog, assetId, cancellationToken);
                resolved.Add(assetId, source);
            }
            var shell = entity.SkyShell!;
            if ((uint)shell.SourceShellIndex >= (uint)source.Skybox.Shells.Count)
                throw new InvalidDataException(
                    $"Sky shell {entity.EntityId} source index {shell.SourceShellIndex} is unavailable.");
            composition.Add(new(
                source.Bytes,
                shell.SourceShellIndex,
                Vector(shell.InitialRotationRadians),
                Vector(shell.AngularVelocityRadiansPerSecond)));
        }

        return await Task.Run(
            () => SkyboxComposer.Compose(GameId.UYA, baseBytes, composition, cancellationToken),
            cancellationToken);
    }

    private static async Task<ResolvedSky> ResolveAsync(
        ForgeProjectWorkspace workspace,
        AssetCatalogStore catalog,
        AssetId assetId,
        CancellationToken cancellationToken)
    {
        var entry = catalog.Query(new(Id: assetId)).SingleOrDefault()
            ?? throw new FileNotFoundException($"Sky asset {assetId} is not present in the catalog.");
        if (entry.Kind != AssetKind.Sky)
            throw new InvalidDataException($"Asset {assetId} is not a sky asset.");
        var source = entry.Sources.Where(value => value.Game == workspace.Manifest.Target.Game
                && value.Region == workspace.Manifest.Target.Region
                && value.Revision == workspace.Manifest.Target.Revision)
            .OrderBy(value => value.Level, StringComparer.Ordinal)
            .ThenBy(value => value.Archive, StringComparer.Ordinal)
            .ThenBy(value => value.SourceIndex)
            .FirstOrDefault()
            ?? throw new InvalidDataException($"Sky asset {assetId} is incompatible with the active project target.");
        var path = workspace.ResolveAssetPath(assetId, catalog)
            ?? throw new FileNotFoundException($"Sky asset blob {assetId} is missing.");
        var bytes = await AssetCatalogBlobReader.ReadVerifiedAsync(
            entry, path, UyaAssetLimits.MaxCanonicalBytes, cancellationToken);
        using var stream = new MemoryStream(bytes, writable: false);
        var skybox = SkyboxReader.Read(stream, GameId.UYA);
        return new(bytes, skybox, ParseLevel(source.Level));
    }

    private static int ParseLevel(string value)
    {
        if (!value.StartsWith("level", StringComparison.Ordinal)
            || !int.TryParse(value.AsSpan(5), out var level) || level < 0)
            throw new InvalidDataException($"Sky asset source level '{value}' is invalid.");
        return level;
    }

    private static ProjectVector3 Radians(short x, short y, short z, float unit) =>
        new(x * unit, y * unit, z * unit);

    private static System.Numerics.Vector3 Vector(ProjectVector3 value) => new(value.X, value.Y, value.Z);

    private static ProjectVector3 Quantize(ProjectVector3 value, float unit, string name) => new(
        Quantize(value.X, unit, $"{name} X"),
        Quantize(value.Y, unit, $"{name} Y"),
        Quantize(value.Z, unit, $"{name} Z"));

    private static float Quantize(float value, float unit, string name)
    {
        if (!float.IsFinite(value)) throw new InvalidDataException($"Sky shell {name} must be finite.");
        var ticks = MathF.Round(value / unit, MidpointRounding.AwayFromZero);
        if (ticks < short.MinValue || ticks > short.MaxValue)
            throw new InvalidDataException($"Sky shell {name} is outside the native signed 16-bit range.");
        return ticks * unit;
    }

    private sealed record ResolvedSky(byte[] Bytes, Skybox Skybox, int Level);
}

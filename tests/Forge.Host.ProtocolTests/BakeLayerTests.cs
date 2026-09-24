using Forge.Host.Games.UYA;
using System.Security.Cryptography;
using System.Text;
using Forge.Host.Domain;

internal static class BakeLayerTests
{
    public static async Task RunAsync()
    {
        VerifyPlanning();
        VerifyValidationDiagnostics();
        await VerifyTransactionalStagingAsync();
    }

    private static void VerifyPlanning()
    {
        var firstAsset = AssetId.Compute(AssetKind.Tie, 0, "first"u8);
        var secondAsset = AssetId.Compute(AssetKind.Texture, 0, "second"u8);
        var inputs = Inputs(firstAsset, secondAsset);
        var context = Context();
        var first = BakeLayerGraph.CreatePlan(context, inputs);

        Equal(true, Enum.GetValues<BakeLayerId>().SequenceEqual(BakeLayerGraph.Definitions.Select(value => value.Id)),
            "all P0 layers declared once");
        Equal(true, first.Layers.All(value => value.State == BakeLayerState.Dirty), "initial plan is dirty");

        var successful = SuccessfulManifest(first);
        var clean = BakeLayerGraph.CreatePlan(context, inputs.Reverse().ToArray(), successful);
        Equal(true, clean.Layers.All(value => value.State == BakeLayerState.Clean), "matching plan is clean");

        var reorderedAssets = inputs.Select(input => input.Id == BakeLayerId.Ties
            ? input with { AssetIds = input.AssetIds.Reverse().ToArray() }
            : input).ToArray();
        var reordered = BakeLayerGraph.CreatePlan(context, reorderedAssets, successful);
        Equal(Layer(clean, BakeLayerId.Ties).InputFingerprint, Layer(reordered, BakeLayerId.Ties).InputFingerprint,
            "asset order does not affect fingerprint");

        var changedInputs = inputs.Select(input => input.Id == BakeLayerId.Ties
            ? input with { AuthoritativeContent = "changed ties"u8.ToArray() }
            : input).ToArray();
        var changed = BakeLayerGraph.CreatePlan(context, changedInputs, successful);
        Equal(BakeLayerState.Dirty, Layer(changed, BakeLayerId.Ties).State, "changed layer is dirty");
        Equal(BakeLayerState.DependencyInvalidated, Layer(changed, BakeLayerId.Lighting).State,
            "dependent layer is invalidated");
        Equal(BakeLayerState.Clean, Layer(changed, BakeLayerId.Shrubs).State, "unrelated layer remains clean");

        var blockedInputs = inputs.Select(input => input.Id == BakeLayerId.Ties
            ? input with { Blockers = ["Missing tie asset."] }
            : input).ToArray();
        var blocked = BakeLayerGraph.CreatePlan(context, blockedInputs, successful);
        Equal(BakeLayerState.Blocked, Layer(blocked, BakeLayerId.Ties).State, "invalid layer is blocked");
        Equal(BakeLayerState.Blocked, Layer(blocked, BakeLayerId.Lighting).State, "blocked dependency propagates");

        var newVersion = BakeLayerGraph.CreatePlan(context with { BakerVersion = "baker-2" }, inputs, successful);
        Equal(true, newVersion.Layers.All(value => value.State == BakeLayerState.Dirty), "baker version invalidates layers");
    }

    private static async Task VerifyTransactionalStagingAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-bake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var inputs = Inputs(
                AssetId.Compute(AssetKind.Tie, 0, "first"u8),
                AssetId.Compute(AssetKind.Texture, 0, "second"u8));
            var initialPlan = BakeLayerGraph.CreatePlan(Context(), inputs);
            var store = await BakeStagingStore.OpenAsync(root);
            var first = await store.CommitAsync(Layer(initialPlan, BakeLayerId.World), (path, cancellationToken) =>
                File.WriteAllTextAsync(Path.Combine(path, "world.bin"), "v1", cancellationToken));
            Equal("v1", await File.ReadAllTextAsync(Path.Combine(store.RootPath, first.RelativePath, "world.bin")),
                "first output committed");

            var changedInputs = inputs.Select(input => input.Id == BakeLayerId.World
                ? input with { AuthoritativeContent = "world-v2"u8.ToArray() }
                : input).ToArray();
            var changedPlan = BakeLayerGraph.CreatePlan(Context(), changedInputs, store.Manifest);
            var changedWorld = Layer(changedPlan, BakeLayerId.World);
            await ThrowsAsync<IOException>(() => store.CommitAsync(changedWorld, async (path, cancellationToken) =>
            {
                await File.WriteAllTextAsync(Path.Combine(path, "world.bin"), "v2", cancellationToken);
                throw new IOException("injected failure");
            }));
            Equal(first.InputFingerprint, store.Manifest.Layers.Single().InputFingerprint,
                "failure retains active manifest");
            Equal("v1", await File.ReadAllTextAsync(Path.Combine(store.RootPath, first.RelativePath, "world.bin")),
                "failure retains prior output");

            using (var cancellation = new CancellationTokenSource())
            {
                await ThrowsAsync<OperationCanceledException>(() => store.CommitAsync(changedWorld, async (path, _) =>
                {
                    await File.WriteAllTextAsync(Path.Combine(path, "world.bin"), "v2");
                    cancellation.Cancel();
                }, cancellation.Token));
            }
            Equal(first.InputFingerprint, store.Manifest.Layers.Single().InputFingerprint,
                "cancellation retains active manifest");

            var second = await store.CommitAsync(changedWorld, (path, cancellationToken) =>
                File.WriteAllTextAsync(Path.Combine(path, "world.bin"), "v2", cancellationToken));
            Equal("v2", await File.ReadAllTextAsync(Path.Combine(store.RootPath, second.RelativePath, "world.bin")),
                "replacement output committed");
            Equal(second.InputFingerprint, store.Manifest.Layers.Single().InputFingerprint,
                "manifest points to replacement");
            Equal(true, Directory.Exists(Path.Combine(store.RootPath, first.RelativePath)),
                "prior snapshot remains recoverable");

            var invalidInputs = inputs.Select(input => input.Id == BakeLayerId.World
                ? input with { AuthoritativeContent = "world-v3"u8.ToArray() }
                : input).ToArray();
            var invalidWorld = Layer(BakeLayerGraph.CreatePlan(Context(), invalidInputs, store.Manifest), BakeLayerId.World);
            await ThrowsAsync<InvalidDataException>(() => store.CommitAsync(
                invalidWorld,
                (path, cancellationToken) => File.WriteAllTextAsync(Path.Combine(path, "world.bin"), "invalid", cancellationToken),
                (_, _) => throw new InvalidDataException("post-write validation failed")));
            Equal(second, store.Manifest.Layers.Single(value => value.Layer == BakeLayerId.World),
                "validation failure retains prior snapshot");

            var lighting = Layer(initialPlan, BakeLayerId.Lighting);
            await store.CommitAsync(lighting, (path, cancellationToken) =>
                File.WriteAllTextAsync(Path.Combine(path, "lighting.bin"), "light-v1", cancellationToken));
            var worldBeforeLighting = store.Manifest.Layers.Single(value => value.Layer == BakeLayerId.World);
            var changedLightingInputs = inputs.Select(input => input.Id == BakeLayerId.Lighting
                ? input with { AuthoritativeContent = "lighting-v2"u8.ToArray() }
                : input).ToArray();
            var changedLighting = Layer(BakeLayerGraph.CreatePlan(Context(), changedLightingInputs, store.Manifest),
                BakeLayerId.Lighting);
            await store.CommitAsync(changedLighting, (path, cancellationToken) =>
                File.WriteAllTextAsync(Path.Combine(path, "lighting.bin"), "light-v2", cancellationToken));
            Equal(worldBeforeLighting,
                store.Manifest.Layers.Single(value => value.Layer == BakeLayerId.World),
                "lighting re-bake leaves world snapshot untouched");
            Equal(0, Directory.EnumerateDirectories(store.RootPath, ".write-*").Count(),
                "temporary writes removed");

            var reopened = await BakeStagingStore.OpenAsync(root);
            Equal(second.OutputFingerprint,
                reopened.Manifest.Layers.Single(value => value.Layer == BakeLayerId.World).OutputFingerprint,
                "committed staging validates on reopen");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyValidationDiagnostics()
    {
        var asset = AssetId.Compute(AssetKind.Tie, 0, "diagnostic asset"u8);
        var entity = new ProjectEntity(
            EntityId.New(), "Broken tie", "ties", ProjectTransform.Identity,
            new(asset, AssetKind.Tie));
        var plan = BakeLayerGraph.CreatePlan(Context(), Inputs(asset, asset).Select(input =>
            input.Id == BakeLayerId.Ties
                ? input with { Blockers = [$"Broken tie ({entity.EntityId}) references missing vanilla asset {asset}."] }
                : input).ToArray());
        var result = UyaBakeValidationService.Validate(Context().Target, plan, [entity]);
        var diagnostic = result.Diagnostics.Single(value => value.Layer == BakeLayerId.Ties);
        Equal(entity.EntityId, diagnostic.EntityId, "diagnostic entity identity");
        Equal(asset, diagnostic.AssetId, "diagnostic asset identity");
        Equal(false, result.CanBake, "blocking diagnostic prevents bake");

        var clean = BakeLayerGraph.CreatePlan(Context(), Inputs(asset, asset));
        var warning = new BakeDiagnostic(
            "TEST_WARNING", BakeDiagnosticSeverity.Warning, BakeLayerId.Lighting, null, null,
            "Test warning.", "Acknowledge it.");
        var pending = UyaBakeValidationService.Validate(Context().Target, clean, [], [warning]);
        Equal(false, pending.CanBake, "unacknowledged warning prevents bake");
        var acknowledged = UyaBakeValidationService.Validate(
            Context().Target, clean, [], [warning], new HashSet<string>([warning.Code], StringComparer.Ordinal));
        Equal(true, acknowledged.CanBake, "acknowledged warning permits bake");

        var unsupported = UyaBakeValidationService.Validate(
            Context().Target with { Region = "PAL" }, clean, []);
        Equal("UYA_TARGET_UNSUPPORTED", unsupported.Diagnostics.Single().Code, "SDK target capability diagnostic");
    }

    private static BakeFingerprintContext Context() => new(
        new("UYA", "NTSC-U", "1.00", "uya-ntsc-u"),
        "translator-1",
        "baker-1");

    private static BakeLayerInput[] Inputs(AssetId firstAsset, AssetId secondAsset) =>
        Enum.GetValues<BakeLayerId>().Select(layer => new BakeLayerInput(
            layer,
            Encoding.UTF8.GetBytes($"{layer}-content"),
            layer == BakeLayerId.Ties ? [firstAsset, secondAsset] : [],
            Encoding.UTF8.GetBytes($"{layer}-settings"))).ToArray();

    private static BakeManifest SuccessfulManifest(BakePlan plan) => new(
        BakeSchema.CurrentVersion,
        BakeSchema.ManifestDocumentType,
        plan.Layers.Select(layer => new BakeLayerSnapshot(
            layer.Layer,
            layer.ContentFingerprint,
            layer.InputFingerprint,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(layer.Layer.ToString()))).ToLowerInvariant(),
            $"layers/{layer.Layer.ToString().ToLowerInvariant()}/{layer.InputFingerprint}",
            1)).ToArray());

    private static BakeLayerPlan Layer(BakePlan plan, BakeLayerId layer) =>
        plan.Layers.Single(value => value.Layer == layer);

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}

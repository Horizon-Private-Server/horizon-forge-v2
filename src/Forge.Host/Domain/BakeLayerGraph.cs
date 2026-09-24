using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Forge.Host.Domain;

public static class BakeLayerGraph
{
    private static readonly BakeLayerDefinition[] LayerDefinitions =
    [
        new(BakeLayerId.World, []),
        new(BakeLayerId.Sky, []),
        new(BakeLayerId.Tfrags, []),
        new(BakeLayerId.Collision, [BakeLayerId.Tfrags]),
        new(BakeLayerId.Ties, []),
        new(BakeLayerId.Shrubs, []),
        new(BakeLayerId.Mobys, []),
        new(BakeLayerId.Gameplay, [BakeLayerId.Mobys]),
        new(BakeLayerId.Lighting,
            [BakeLayerId.World, BakeLayerId.Tfrags, BakeLayerId.Ties, BakeLayerId.Shrubs, BakeLayerId.Mobys]),
        new(BakeLayerId.Opaque, []),
    ];

    public static IReadOnlyList<BakeLayerDefinition> Definitions => LayerDefinitions;

    public static BakePlan CreatePlan(
        BakeFingerprintContext context,
        IReadOnlyList<BakeLayerInput> inputs,
        BakeManifest? previous = null,
        bool rebuildAll = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(inputs);
        ValidateContext(context);
        ValidateManifest(previous);

        var inputByLayer = inputs.ToDictionary(input => input.Id);
        var previousByLayer = previous?.Layers.ToDictionary(snapshot => snapshot.Layer)
            ?? new Dictionary<BakeLayerId, BakeLayerSnapshot>();
        var plans = new List<BakeLayerPlan>(LayerDefinitions.Length);
        var planByLayer = new Dictionary<BakeLayerId, BakeLayerPlan>();

        foreach (var definition in LayerDefinitions)
        {
            inputByLayer.TryGetValue(definition.Id, out var input);
            var blockers = input?.Blockers?.Where(value => !string.IsNullOrWhiteSpace(value)).ToList() ?? [];
            if (input is null) blockers.Add("Layer input is unavailable.");
            foreach (var dependency in definition.Dependencies)
            {
                if (planByLayer[dependency].State == BakeLayerState.Blocked)
                    blockers.Add($"Dependency {dependency} is blocked.");
            }

            var contentFingerprint = input is null ? string.Empty : FingerprintContent(context, input);
            var inputFingerprint = blockers.Count == 0
                ? FingerprintInput(contentFingerprint, definition.Dependencies, planByLayer)
                : string.Empty;
            var state = StateFor(
                blockers,
                contentFingerprint,
                inputFingerprint,
                previousByLayer.GetValueOrDefault(definition.Id),
                rebuildAll);
            var plan = new BakeLayerPlan(
                definition.Id,
                state,
                contentFingerprint,
                inputFingerprint,
                definition.Dependencies,
                blockers);
            plans.Add(plan);
            planByLayer.Add(definition.Id, plan);
        }

        var unknown = inputByLayer.Keys.Except(LayerDefinitions.Select(value => value.Id)).ToArray();
        if (unknown.Length > 0) throw new ArgumentException($"Unknown bake layer: {unknown[0]}.", nameof(inputs));
        return new(plans);
    }

    private static BakeLayerState StateFor(
        IReadOnlyList<string> blockers,
        string contentFingerprint,
        string inputFingerprint,
        BakeLayerSnapshot? previous,
        bool rebuildAll)
    {
        if (blockers.Count > 0) return BakeLayerState.Blocked;
        if (rebuildAll || previous is null || previous.ContentFingerprint != contentFingerprint)
            return BakeLayerState.Dirty;
        return previous.InputFingerprint == inputFingerprint
            ? BakeLayerState.Clean
            : BakeLayerState.DependencyInvalidated;
    }

    private static string FingerprintContent(BakeFingerprintContext context, BakeLayerInput input) =>
        Hash("HorizonForgeBakeLayerContent\0", hash =>
        {
            AppendInt(hash, (int)input.Id);
            AppendString(hash, context.Target.Game);
            AppendString(hash, context.Target.Region);
            AppendString(hash, context.Target.Revision);
            AppendString(hash, context.Target.BakeProfile);
            AppendString(hash, context.TranslatorVersion);
            AppendString(hash, context.BakerVersion);
            AppendBytes(hash, input.RelevantSettings.Span);
            AppendBytes(hash, input.AuthoritativeContent.Span);
            foreach (var assetId in input.AssetIds.Select(value => value.ToString()).Distinct().Order(StringComparer.Ordinal))
            {
                if (!BakeSchema.IsFingerprint(assetId)) throw new ArgumentException("Layer contains an invalid Asset ID.");
                AppendString(hash, assetId);
            }
        });

    private static string FingerprintInput(
        string contentFingerprint,
        IReadOnlyList<BakeLayerId> dependencies,
        IReadOnlyDictionary<BakeLayerId, BakeLayerPlan> plans) =>
        Hash("HorizonForgeBakeLayerInput\0", hash =>
        {
            AppendString(hash, contentFingerprint);
            foreach (var dependency in dependencies)
            {
                AppendInt(hash, (int)dependency);
                AppendString(hash, plans[dependency].InputFingerprint);
            }
        });

    private static string Hash(string domain, Action<IncrementalHash> append)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(domain));
        append(hash);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendString(IncrementalHash hash, string value) =>
        AppendBytes(hash, Encoding.UTF8.GetBytes(value));

    private static void AppendBytes(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static void AppendInt(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void ValidateContext(BakeFingerprintContext context)
    {
        ArgumentNullException.ThrowIfNull(context.Target);
        if (new[] { context.Target.Game, context.Target.Region, context.Target.Revision, context.Target.BakeProfile }
            .Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Target fields are required.", nameof(context));
        if (string.IsNullOrWhiteSpace(context.TranslatorVersion))
            throw new ArgumentException("Translator version is required.", nameof(context));
        if (string.IsNullOrWhiteSpace(context.BakerVersion))
            throw new ArgumentException("Baker version is required.", nameof(context));
    }

    private static void ValidateManifest(BakeManifest? manifest)
    {
        if (manifest is null) return;
        if (manifest.SchemaVersion != BakeSchema.CurrentVersion || manifest.DocumentType != BakeSchema.ManifestDocumentType)
            throw new InvalidDataException("Bake manifest schema is unsupported.");
        if (manifest.Layers.Select(value => value.Layer).Distinct().Count() != manifest.Layers.Count)
            throw new InvalidDataException("Bake manifest contains duplicate layers.");
        if (manifest.PaletteReport is not null)
            PaletteBakeReportService.ValidateForCommit(manifest.PaletteReport);
        if (manifest.Layers.Any(value => !Enum.IsDefined(value.Layer)
            || !BakeSchema.IsFingerprint(value.ContentFingerprint)
            || !BakeSchema.IsFingerprint(value.InputFingerprint)))
            throw new InvalidDataException("Bake manifest contains an invalid layer snapshot.");
    }
}

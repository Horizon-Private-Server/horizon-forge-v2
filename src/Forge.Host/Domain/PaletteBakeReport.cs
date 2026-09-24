using RatchetPs2.Core.Textures.Palettes;

namespace Forge.Host.Domain;

public sealed record PaletteBakeReport(
    int SchemaVersion,
    int InputTextureCount,
    int InputPaletteCount,
    int OutputPaletteCount,
    long InputPaletteBytes,
    long OutputPaletteBytes,
    long EstimatedPaletteVramSavingsBytes,
    bool IsLossless,
    double ImportedQuantizationError,
    PaletteOptimizationResult Optimization);

public static class PaletteBakeReportService
{
    public const int CurrentVersion = 1;

    public static PaletteBakeReport Create(
        TextureInventory inventory,
        CancellationToken cancellationToken = default) =>
        Create(inventory, PaletteOptimizer.Optimize(inventory, cancellationToken));

    public static PaletteBakeReport Create(
        TextureInventory inventory,
        PaletteOptimizationResult optimization)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(optimization);
        var sourcePalettes = inventory.Textures
            .GroupBy(SourcePaletteKey, StringComparer.Ordinal)
            .Select(group => group.First().Constraint.PaletteEntryCount * 4L)
            .ToArray();
        var outputBytes = optimization.Palettes.Sum(value => value.Capacity * 4L);
        var report = new PaletteBakeReport(
            CurrentVersion,
            inventory.Textures.Count,
            sourcePalettes.Length,
            optimization.Palettes.Count,
            sourcePalettes.Sum(),
            outputBytes,
            sourcePalettes.Sum() - outputBytes,
            optimization.Violations.Count == 0,
            0,
            optimization);
        ValidateForCommit(report);
        return report;
    }

    public static void ValidateForCommit(PaletteBakeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(report.Optimization);
        var optimization = report.Optimization;
        ArgumentNullException.ThrowIfNull(optimization.Palettes);
        ArgumentNullException.ThrowIfNull(optimization.Assignments);
        ArgumentNullException.ThrowIfNull(optimization.Violations);
        var methods = new[]
        {
            PaletteOptimizer.ExactMethod,
            PaletteOptimizer.HeuristicMethod,
            PaletteOptimizer.HybridMethod,
        };
        if (report.SchemaVersion != CurrentVersion
            || report.InputTextureCount < 0
            || report.InputPaletteCount < 0
            || report.InputPaletteCount > report.InputTextureCount
            || (report.InputTextureCount == 0) != (report.InputPaletteCount == 0)
            || report.OutputPaletteCount != optimization.Palettes.Count
            || report.InputPaletteBytes < 0
            || report.OutputPaletteBytes != optimization.Palettes.Sum(value => value.Capacity * 4L)
            || report.EstimatedPaletteVramSavingsBytes != report.InputPaletteBytes - report.OutputPaletteBytes
            || !report.IsLossless
            || report.ImportedQuantizationError != 0
            || optimization.SchemaVersion != PaletteOptimizer.SchemaVersion
            || !methods.Contains(optimization.Method, StringComparer.Ordinal)
            || optimization.Violations.Count != 0
            || optimization.Assignments.Count != report.InputTextureCount
            || optimization.Assignments.Select(value => value.TextureKey).Distinct(StringComparer.Ordinal).Count()
                != optimization.Assignments.Count)
            throw new InvalidDataException("Palette report is invalid or incomplete.");

        var palettes = optimization.Palettes.ToDictionary(value => value.PaletteIndex);
        if (palettes.Count != optimization.Palettes.Count
            || optimization.Assignments.Any(value => value.IndexRemaps is null
                || !palettes.TryGetValue(value.PaletteIndex, out var palette)
                || palette.TextureKeys is null
                || !palette.TextureKeys.Contains(value.TextureKey, StringComparer.Ordinal)
                || value.IndexRemaps.Any(remap => remap.SourcePixelIndex is < 0 or > 255
                    || remap.SourcePaletteIndex is < 0 or > 255
                    || remap.TargetPaletteIndex < 0
                    || remap.TargetPaletteIndex >= palette.Capacity)
                || value.IndexRemaps.Select(remap => remap.SourcePixelIndex).Distinct().Count()
                    != value.IndexRemaps.Count)
            || optimization.Palettes.Any(value => value.Capacity is <= 0 or > 256
                || value.Entries is null
                || value.TextureKeys is null
                || value.Entries.Count > value.Capacity
                || value.Entries.Any(entry => entry.PaletteIndex < 0 || entry.PaletteIndex >= value.Capacity)
                || value.Entries.Select(entry => entry.PaletteIndex).Distinct().Count() != value.Entries.Count)
            || optimization.Palettes.SelectMany(value => value.TextureKeys).Count()
                != optimization.Assignments.Count)
            throw new InvalidDataException("Palette report contains invalid assignments or palette entries.");
    }

    public static bool Equivalent(PaletteBakeReport left, PaletteBakeReport right) =>
        ForgeProjectPersistence.Serialize(left).AsSpan().SequenceEqual(ForgeProjectPersistence.Serialize(right));

    private static string SourcePaletteKey(TextureInventoryEntry texture)
    {
        var colors = new byte[texture.PaletteEntries.Count * 4];
        foreach (var entry in texture.PaletteEntries.OrderBy(value => value.PaletteIndex))
        {
            var offset = entry.PaletteIndex * 4;
            colors[offset] = entry.Color.Red;
            colors[offset + 1] = entry.Color.Green;
            colors[offset + 2] = entry.Color.Blue;
            colors[offset + 3] = entry.Color.Alpha;
        }
        return $"{texture.Constraint.Encoding}:{texture.Constraint.PaletteFormat}:"
            + $"{texture.Constraint.PaletteOrder}:{Convert.ToHexString(colors)}";
    }
}

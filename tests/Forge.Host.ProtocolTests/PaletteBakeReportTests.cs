using System.Text.Json.Nodes;
using Forge.Host.Domain;
using RatchetPs2.Core.Textures.Palettes;
using RatchetPs2.Core.Textures.Pif;

internal static class PaletteBakeReportTests
{
    public static void Run()
    {
        var first = new TextureColor(1, 2, 3, 4);
        var second = new TextureColor(5, 6, 7, 8);
        var report = new PaletteBakeReport(1, 2, 2, 2, 2_048, 2_048, 0, true, 0,
            new(1, PaletteOptimizer.ExactMethod, true,
            [
                new(0, PifTextureEncoding.Indexed8, 0, 1, 256,
                    [new(0, first, true), new(1, second, false)], ["first", "second"]),
                new(1, PifTextureEncoding.Indexed4, 1, 0, 16,
                    [new(0, second, false)], ["third"]),
            ],
            [
                new("first", 0, [new(0, 1, 2, first), new(3, 4, 5, second)]),
                new("third", 1, [new(6, 7, 8, second)]),
            ],
            [new("first", "first-code", "first message"), new("third", "second-code", "second message")]));
        var original = ForgeProjectPersistence.Serialize(report);
        var root = JsonNode.Parse(original)!;
        Check(root);
        Visit(root);
        Check(JsonNode.Parse(ForgeProjectPersistence.Serialize(report with { ImportedQuantizationError = -0.0 }))!);
        Check(JsonNode.Parse(ForgeProjectPersistence.Serialize(report with
        {
            Optimization = report.Optimization with { Palettes = [], Assignments = [], Violations = [] },
        }))!);

        void Check(JsonNode candidate)
        {
            var copy = ForgeProjectPersistence.Deserialize<PaletteBakeReport>(
                System.Text.Encoding.UTF8.GetBytes(candidate.ToJsonString()), "palette comparison fixture");
            var expected = original.AsSpan().SequenceEqual(ForgeProjectPersistence.Serialize(copy));
            if (PaletteBakeReportService.Equivalent(report, copy) != expected
                || PaletteBakeReportService.Equivalent(copy, report) != expected)
                throw new InvalidOperationException("Palette report comparison differs from serialized equality.");
        }

        void Visit(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                foreach (var (_, child) in obj.ToArray()) Visit(child!);
            }
            else if (node is JsonArray array)
            {
                if (array.Count > 1)
                    ReplaceAndCheck(array, new JsonArray(array.Reverse().Select(value => value!.DeepClone()).ToArray()));
                ReplaceAndCheck(array, new JsonArray());
                ReplaceAndCheck(array, new JsonArray(array.Append(array[0]).Select(value => value!.DeepClone()).ToArray()));
                foreach (var child in array.ToArray()) Visit(child!);
            }
            else if (node is JsonValue value)
            {
                JsonNode changed = value.TryGetValue<bool>(out var boolean) ? JsonValue.Create(!boolean)!
                    : value.TryGetValue<int>(out var number) ? JsonValue.Create(number + 1)!
                    : JsonValue.Create(value.GetValue<string>() switch
                    {
                        "Indexed8" => "Indexed4",
                        "Indexed4" => "Indexed8",
                        var text => text + " changed",
                    })!;
                ReplaceAndCheck(node, changed);
            }
        }

        void ReplaceAndCheck(JsonNode node, JsonNode changed)
        {
            node.ReplaceWith(changed);
            Check(root);
            changed.ReplaceWith(node);
        }
    }
}

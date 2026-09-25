using Forge.Host.Domain;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Games.UYA.Level;

namespace Forge.Host.Games.UYA;

internal static class UyaOpaqueContentService
{
    private static readonly HashSet<string> ParsedGameplaySections =
    [
        "level_settings",
        "tie_instances",
        "shrub_instances",
        "moby_instances",
        "pvar_moby_links",
        "pvar_table",
        "pvar_data",
        "pvar_relative_pointers",
        "directional_lights",
        "point_lights",
        "tie_ambient_rgbas",
        "cameras",
        "sound_instances",
        "cuboids",
        "spheres",
        "cylinders",
        "pills",
        "splines",
    ];

    public static IReadOnlyList<OpaqueSectionCapture> Capture(
        ReadOnlySpan<byte> levelWadBytes,
        UyaLevelWadPackage package)
    {
        var sections = new List<OpaqueSectionCapture>();
        AddSector(sections, "level/sound-bank", "level_wad", 0x18, package.LevelWad.SoundBank, levelWadBytes);
        AddSector(sections, "level/occlusion", "level_wad", 0x28, package.LevelWad.Occlusion, levelWadBytes);

        var files = package.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        if (files.TryGetValue("level_wad/level_data.wad", out var levelDataFile))
        {
            var levelData = UyaLevelWadReader.ReadLevelDataWad(levelDataFile.Bytes);
            AddByte(sections, "level-data/code-overlay", "level_wad/level_data.wad", 0x00,
                levelData.Overlay, levelDataFile.Bytes);
            AddByte(sections, "level-data/hud-header", "level_wad/level_data.wad", 0x18,
                levelData.HudHeader, levelDataFile.Bytes);
            for (var index = 0; index < levelData.HudBanks.Count; index++)
                AddByte(sections, $"level-data/hud-bank-{index}", "level_wad/level_data.wad", 0x20 + index * 8,
                    levelData.HudBanks[index], levelDataFile.Bytes);
            AddByte(sections, "level-data/transition-textures", "level_wad/level_data.wad", 0x50,
                levelData.TransitionTextures, levelDataFile.Bytes);
        }

        if (files.TryGetValue("gameplay/gameplay_core.bin", out var gameplayFile))
        {
            foreach (var block in UyaGameplayBlockReader.ReadCore(gameplayFile.Bytes).Blocks
                .Where(value => value.PayloadBytes.Length > 0 && !ParsedGameplaySections.Contains(value.SemanticName)))
            {
                sections.Add(new(
                    $"gameplay/{block.SemanticName}",
                    new("gameplay/gameplay_core.bin", block.HeaderOffset, block.Pointer, block.PayloadBytes.LongLength, 1),
                    block.PayloadBytes));
            }
        }
        return sections;
    }

    private static void AddSector(
        ICollection<OpaqueSectionCapture> sections,
        string name,
        string container,
        int headerOffset,
        UyaFileBlock block,
        ReadOnlySpan<byte> bytes)
    {
        var payload = UyaLevelWadReader.ReadSectorFileBlock(bytes, block);
        if (payload.Length > 0)
            sections.Add(new(name, new(container, headerOffset, block.OffsetBytes, payload.LongLength,
                UyaLevelConstants.SectorSize), payload));
    }

    private static void AddByte(
        ICollection<OpaqueSectionCapture> sections,
        string name,
        string container,
        int headerOffset,
        UyaByteBlock block,
        ReadOnlySpan<byte> bytes)
    {
        var payload = UyaLevelWadReader.ReadByteFileBlock(bytes, block);
        if (payload.Length > 0)
            sections.Add(new(name, new(container, headerOffset, block.Offset, payload.LongLength, 1), payload));
    }
}

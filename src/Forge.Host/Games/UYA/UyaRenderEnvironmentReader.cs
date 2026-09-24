using Forge.Host.Domain;
using RatchetPs2.Games.UYA.Gameplay;
using RatchetPs2.Games.UYA.Level;

namespace Forge.Host.Games.UYA;

internal static class UyaRenderEnvironmentReader
{
    private const float DistanceScale = 1f / 1024f;

    public static UyaRenderEnvironmentResult? Read(byte[] levelWad)
    {
        var file = UyaLevelWadUnpacker.Unpack(levelWad).Files
            .SingleOrDefault(value => value.Path == "gameplay/core/level_settings.bin");
        if (file is null || !UyaLevelSettingsReader.TryRead(file.Bytes, out var settings) || settings is null) return null;
        return new(
            Channel(settings.BackgroundColor.Red),
            Channel(settings.BackgroundColor.Green),
            Channel(settings.BackgroundColor.Blue),
            Channel(settings.FogColor.Red),
            Channel(settings.FogColor.Green),
            Channel(settings.FogColor.Blue),
            Distance(settings.FogNearDistance),
            Distance(settings.FogFarDistance),
            Finite(settings.FogNearIntensity),
            Finite(settings.FogFarIntensity));
    }

    private static uint Channel(int value) => (uint)Math.Clamp(value, 0, 255);
    private static float Distance(float value) => Math.Max(0, Finite(value) * DistanceScale);
    private static float Finite(float value) => float.IsFinite(value) ? value : 0;
}

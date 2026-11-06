using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal static class UyaNativeContentCompatibility
{
    public static bool Supports(ProjectEntityProvenance? source) =>
        source is { Game: "UYA" or "GC" or "RC1" or "DL" };
}

using RatchetPs2.Core.Games;

namespace Forge.Host.Games.UYA;

internal sealed record UyaContentImportDonorProfile(string Revision, int MaximumLevel)
{
    public static UyaContentImportDonorProfile For(GameId game) => game switch
    {
        GameId.GC => new("1.01", 99),
        GameId.RC1 => new("1.00", 18),
        GameId.DL => new("1.00", 79),
        _ => throw new NotSupportedException($"Converted content imports from {game} are not supported."),
    };
}

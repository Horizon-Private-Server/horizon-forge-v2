using Forge.Host.Domain;
using RatchetPs2.Core.Wad.Models;

namespace Forge.Host.Games.UYA;

public sealed record UyaLevelPackResult(
    bool Succeeded,
    byte[]? OutputBytes,
    string? OutputSha256,
    IReadOnlyList<LevelArchiveChange> Changes,
    IReadOnlyList<LevelArchiveCompression> Compressions,
    IReadOnlyList<BakeDiagnostic> Diagnostics);

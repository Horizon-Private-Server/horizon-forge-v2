using Forge.Host.Domain;
using RatchetPs2.Sdk;

namespace Forge.Host.Games.UYA;

internal static class UyaCollisionAdapter
{
    public static ProjectCollisionPieceKind ToProjectKind(CollisionPieceKind kind) => kind switch
    {
        CollisionPieceKind.Solid => ProjectCollisionPieceKind.Solid,
        CollisionPieceKind.PlayerBarrier => ProjectCollisionPieceKind.PlayerBarrier,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown SDK collision piece kind."),
    };

    public static CollisionPieceKind ToSdkKind(ProjectCollisionPieceKind kind) => kind switch
    {
        ProjectCollisionPieceKind.Solid => CollisionPieceKind.Solid,
        ProjectCollisionPieceKind.PlayerBarrier => CollisionPieceKind.PlayerBarrier,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown project collision piece kind."),
    };
}

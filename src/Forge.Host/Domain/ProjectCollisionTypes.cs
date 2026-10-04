namespace Forge.Host.Domain;

public enum ProjectCollisionPieceKind : byte
{
    Solid = 1,
    PlayerBarrier = 2,
}

public sealed record ProjectCollisionTypeCount(byte RawType, int Count);

public sealed record ProjectCollisionAttachment(
    EntityId TieEntityId,
    ProjectTransform BindTransform);

public sealed record ProjectCollisionPiece(
    ProjectCollisionPieceKind Kind,
    int SourcePayloadIndex,
    int SourcePieceIndex,
    int FaceCount,
    int VertexCount,
    IReadOnlyList<ProjectCollisionTypeCount> Types,
    ProjectCollisionAttachment? Attachment = null);

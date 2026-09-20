namespace Forge.Host.Bridge;

public enum BridgeMessageKind : byte
{
    Handshake = 1,
    Request = 2,
    Result = 3,
    Progress = 4,
    Cancel = 5,
    Error = 6,
}

public enum BridgeOpcode : ushort
{
    Control = 0,
    Echo = 1,
    ValidateUyaIso = 2,
    CreateDevelopmentIso = 3,
    ImportUyaAssets = 4,
    ListUyaProjectLevels = 5,
    CreateUyaProject = 6,
    InspectForgeProject = 7,
    RenameForgeProject = 8,
    PreflightUyaProject = 9,
}

public enum BridgeErrorCode : ushort
{
    None = 0,
    InvalidMagic = 1,
    UnsupportedVersion = 2,
    InvalidMessageKind = 3,
    InvalidFlags = 4,
    UnknownOpcode = 5,
    InvalidStatus = 6,
    InvalidRequestId = 7,
    PayloadTooLarge = 8,
    InvalidHeader = 9,
    MalformedPayload = 10,
    Cancelled = 11,
    InternalError = 12,
    InvalidInput = 13,
    Conflict = 14,
    InsufficientSpace = 15,
}

public sealed record BridgeFrame(
    BridgeMessageKind Kind,
    BridgeOpcode Opcode,
    BridgeErrorCode Status,
    uint RequestId,
    byte[] Payload);

public sealed class BridgeProtocolException(BridgeErrorCode code, string message) : Exception(message)
{
    public BridgeErrorCode Code { get; } = code;
}

internal readonly record struct DecodedHeader(
    BridgeMessageKind Kind,
    BridgeOpcode Opcode,
    BridgeErrorCode Status,
    uint RequestId,
    int PayloadLength);

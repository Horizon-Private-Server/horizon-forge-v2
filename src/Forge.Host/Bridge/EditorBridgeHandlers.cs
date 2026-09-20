using Forge.Host.Domain;

namespace Forge.Host.Bridge;

internal static class EditorBridgeHandlers
{
    public static async Task<byte[]> HandleAsync(
        BridgeFrame frame,
        EditorRuntime runtime,
        CancellationToken cancellationToken) => frame.Opcode switch
    {
        BridgeOpcode.OpenEditorProject => await OpenAsync(frame.Payload, runtime, cancellationToken),
        BridgeOpcode.CloseEditorProject => await CloseAsync(frame.Payload, runtime, cancellationToken),
        BridgeOpcode.QueryEditor => await QueryAsync(frame.Payload, runtime, cancellationToken),
        BridgeOpcode.ExecuteEditorCommand => await ExecuteAsync(frame.Payload, runtime, cancellationToken),
        BridgeOpcode.SaveEditorProject => await SaveAsync(frame.Payload, runtime, cancellationToken),
        BridgeOpcode.ReadEditorEvents => await ReadEventsAsync(frame.Payload, runtime, cancellationToken),
        _ => throw new BridgeProtocolException(BridgeErrorCode.UnknownOpcode, $"Unsupported editor opcode: {frame.Opcode}"),
    };

    private static async Task<byte[]> OpenAsync(
        byte[] payload,
        EditorRuntime runtime,
        CancellationToken cancellationToken)
    {
        var request = EditorPayloadCodec.DecodeOpenRequest(payload);
        return EditorPayloadCodec.EncodeSnapshot(await runtime.OpenAsync(
            request.ProjectPath, TimeSpan.FromSeconds(request.AutosaveSeconds), cancellationToken));
    }

    private static async Task<byte[]> CloseAsync(
        byte[] payload,
        EditorRuntime runtime,
        CancellationToken cancellationToken)
    {
        RequireEmpty(payload);
        await runtime.CloseAsync(cancellationToken);
        return BridgePayloadCodec.EncodeText("closed");
    }

    private static async Task<byte[]> QueryAsync(
        byte[] payload,
        EditorRuntime runtime,
        CancellationToken cancellationToken)
    {
        RequireEmpty(payload);
        return EditorPayloadCodec.EncodeSnapshot(await runtime.GetSnapshotAsync(cancellationToken));
    }

    private static async Task<byte[]> ExecuteAsync(
        byte[] payload,
        EditorRuntime runtime,
        CancellationToken cancellationToken) => EditorPayloadCodec.EncodeSnapshot(
            await runtime.ExecuteAsync(EditorPayloadCodec.DecodeCommand(payload), cancellationToken));

    private static async Task<byte[]> SaveAsync(
        byte[] payload,
        EditorRuntime runtime,
        CancellationToken cancellationToken)
    {
        RequireEmpty(payload);
        return EditorPayloadCodec.EncodeSnapshot(await runtime.SaveAsync(cancellationToken));
    }

    private static async Task<byte[]> ReadEventsAsync(
        byte[] payload,
        EditorRuntime runtime,
        CancellationToken cancellationToken)
    {
        var request = EditorPayloadCodec.DecodeEventRequest(payload);
        return EditorPayloadCodec.EncodeEvents(await runtime.ReadEventsAsync(
            checked((long)request.AfterSequence), checked((int)request.Limit), cancellationToken));
    }

    private static void RequireEmpty(byte[] payload)
    {
        if (payload.Length != 0) PayloadFormat.Malformed("Request payload must be empty");
    }
}

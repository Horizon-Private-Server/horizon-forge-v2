using Forge.Host.Domain;

namespace Forge.Host.Bridge;

internal static class SoundBridgeHandlers
{
    public static async Task<byte[]> HandleAsync(
        BridgeFrame frame,
        EditorRuntime runtime,
        CancellationToken cancellationToken) => frame.Opcode switch
    {
        BridgeOpcode.QuerySoundWorkspace => await runtime.ReadWorkspaceAsync(
            async (workspace, token) => SoundPayloadCodec.EncodeWorkspace(
                await SoundWorkspaceService.ReadAsync(
                    RequireEmpty(frame.Payload, workspace), token)), cancellationToken),
        BridgeOpcode.RenderSoundPreview => await RenderPreviewAsync(frame.Payload, runtime, cancellationToken),
        _ => throw new BridgeProtocolException(BridgeErrorCode.UnknownOpcode, $"Unsupported sound opcode: {frame.Opcode}"),
    };

    private static async Task<byte[]> RenderPreviewAsync(
        byte[] payload,
        EditorRuntime runtime,
        CancellationToken cancellationToken)
    {
        var request = SoundPayloadCodec.DecodePreviewRequest(payload);
        if (request.BankAssetId is not null)
            return SoundPayloadCodec.EncodePreview(await SoundWorkspaceService.RenderCatalogPreviewAsync(
                request.CatalogRootPath!, AssetId.Parse(request.BankAssetId), request.Game!,
                request.NativeIndex, request.PreferAudible, cancellationToken));
        return await runtime.ReadWorkspaceAsync(async (workspace, token) =>
            SoundPayloadCodec.EncodePreview(await SoundWorkspaceService.RenderPreviewAsync(
                workspace, request.NativeIndex, request.PreferAudible, token)), cancellationToken);
    }

    private static ForgeProjectWorkspace RequireEmpty(byte[] payload, ForgeProjectWorkspace workspace)
    {
        if (payload.Length != 0) PayloadFormat.Malformed("Sound workspace request payload must be empty");
        return workspace;
    }
}

using Forge.Host.Domain;
using Forge.Host.Games.DL;
using Forge.Host.Games.GC;
using Forge.Host.Games.RC1;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Sdk;

namespace Forge.Host.Bridge;

internal static class SetupBridgeHandlers
{
    public static async Task<byte[]> HandleAsync(
        BridgeFrame frame,
        string sdkRevision,
        Func<IsoProgress, ValueTask> progress,
        CancellationToken cancellationToken)
    {
        // PayloadReader is stack-only; finish boundary decoding before awaiting any work.
        var request = Decode(frame);
        if (frame.Opcode == BridgeOpcode.ListSourceLevels)
        {
            await using var iso = new FileStream(
                request.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            cancellationToken.ThrowIfCancellationRequested();
            var levels = LevelCatalogReader.FindAvailable(request.Game, iso);
            cancellationToken.ThrowIfCancellationRequested();
            var writer = new PayloadWriter();
            writer.WriteUInt32s(levels.Select(level => checked((uint)level)).ToArray());
            return writer.ToArray();
        }
        if (frame.Opcode == BridgeOpcode.ValidateSourceIso)
        {
            var identity = await IsoSourceService.ValidateAsync(request.Game, request.SourcePath, progress, cancellationToken);
            return BridgePayloadCodec.EncodeIsoValidation(new(
                identity.IsSupported, identity.Game, identity.Region, identity.Revision, identity.Serial,
                checked((ulong)identity.Size), identity.Fingerprint, identity.Diagnostic));
        }
        var result = request.Game switch
        {
            GameId.UYA => await UyaAssetImportService.ImportAsync(new(
                request.SourcePath, request.CatalogRoot, request.Fingerprint, request.Revision,
                UyaAssetImportService.ImporterVersionPrefix + sdkRevision, request.Force), progress, cancellationToken),
            GameId.DL => await DlAssetImportService.ImportAsync(new(
                GameId.DL, request.SourcePath, request.CatalogRoot, request.Fingerprint, request.Revision,
                DlAssetImportService.ImporterVersionPrefix + sdkRevision, request.Force), progress, cancellationToken),
            GameId.GC => await GcAssetImportService.ImportAsync(new(
                GameId.GC, request.SourcePath, request.CatalogRoot, request.Fingerprint, request.Revision,
                GcAssetImportService.ImporterVersionPrefix + sdkRevision, request.Force), progress, cancellationToken),
            GameId.RC1 => await Rc1AssetImportService.ImportAsync(new(
                GameId.RC1, request.SourcePath, request.CatalogRoot, request.Fingerprint, request.Revision,
                Rc1AssetImportService.ImporterVersionPrefix + sdkRevision, request.Force), progress, cancellationToken),
            _ => throw new ArgumentException("Unsupported asset source game."),
        };
        return BridgePayloadCodec.EncodeAssetImportResult(new(
            checked((uint)result.CompletedLevels), checked((uint)result.TotalLevels),
            checked((uint)result.AssetAppearances), checked((uint)result.UniqueAssets),
            checked((uint)result.FailedAssets), result.Resumed));
    }

    private static SetupRequest Decode(BridgeFrame frame)
    {
        var reader = new PayloadReader(frame.Payload);
        var game = reader.ReadString() switch
        {
            "UYA" => GameId.UYA,
            "DL" => GameId.DL,
            "GC" => GameId.GC,
            "RC1" => GameId.RC1,
            _ => throw new ArgumentException("Unsupported asset source game."),
        };
        var source = reader.ReadString();
        var request = frame.Opcode is BridgeOpcode.ValidateSourceIso or BridgeOpcode.ListSourceLevels
            ? new SetupRequest(game, source, "", "", "", false)
            : new SetupRequest(game, source, reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadBoolean());
        reader.Complete();
        return request;
    }

    private sealed record SetupRequest(GameId Game, string SourcePath, string CatalogRoot, string Fingerprint, string Revision, bool Force);
}

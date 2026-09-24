using System.Collections.Concurrent;
using Forge.Host.Domain;
using Forge.Host.Games.UYA;

namespace Forge.Host.Bridge;

public static class BridgeHost
{
    public static async Task<int> RunAsync(
        Stream input,
        Stream output,
        TextWriter diagnostics,
        HostHandshake handshake,
        CancellationToken cancellationToken = default)
    {
        var writer = new FrameWriter(output);
        await using var editor = new EditorRuntime();
        var requests = new ConcurrentDictionary<uint, CancellationTokenSource>();
        var tasks = new ConcurrentDictionary<uint, Task>();

        await writer.WriteAsync(new(
            BridgeMessageKind.Handshake,
            BridgeOpcode.Control,
            BridgeErrorCode.None,
            0,
            BridgePayloadCodec.EncodeHandshake(handshake)), cancellationToken);

        var decoder = new BridgeFrameDecoder();
        var buffer = GC.AllocateUninitializedArray<byte>(16 * 1024);

        try
        {
            int bytesRead;
            while ((bytesRead = await input.ReadAsync(buffer, cancellationToken)) != 0)
            {
                foreach (var frame in decoder.Push(buffer.AsSpan(0, bytesRead)))
                {
                    if (frame.Kind == BridgeMessageKind.Cancel)
                    {
                        if (requests.TryGetValue(frame.RequestId, out var active)) active.Cancel();
                        continue;
                    }

                    if (frame.Kind != BridgeMessageKind.Request)
                    {
                        await WriteErrorAsync(writer, frame.RequestId, BridgeErrorCode.InvalidMessageKind,
                            "Host accepts only request and cancellation frames", cancellationToken);
                        continue;
                    }

                    var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    if (!requests.TryAdd(frame.RequestId, requestCancellation))
                    {
                        requestCancellation.Dispose();
                        await WriteErrorAsync(writer, frame.RequestId, BridgeErrorCode.InvalidRequestId,
                            "Request ID is already active", cancellationToken);
                        continue;
                    }

                    var task = HandleRequestAsync(
                        frame, writer, requests, requestCancellation, diagnostics, handshake, editor, cancellationToken);
                    tasks[frame.RequestId] = task;
                    _ = task.ContinueWith(
                        _completed => tasks.TryRemove(frame.RequestId, out _),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
            decoder.Complete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (BridgeProtocolException exception)
        {
            await diagnostics.WriteLineAsync($"Bridge protocol error {exception.Code}: {exception.Message}");
            return 1;
        }
        finally
        {
            foreach (var request in requests.Values) request.Cancel();
        }

        var remaining = tasks.Values.ToArray();
        try
        {
            await Task.WhenAll(remaining);
        }
        catch (OperationCanceledException)
        {
        }
        return 0;
    }

    private static async Task HandleRequestAsync(
        BridgeFrame frame,
        FrameWriter writer,
        ConcurrentDictionary<uint, CancellationTokenSource> requests,
        CancellationTokenSource requestCancellation,
        TextWriter diagnostics,
        HostHandshake handshake,
        EditorRuntime editor,
        CancellationToken hostCancellation)
    {
        try
        {
            switch (frame.Opcode)
            {
                case BridgeOpcode.Echo:
                    await HandleEchoAsync(frame, writer, requestCancellation.Token, hostCancellation);
                    break;
                case BridgeOpcode.ValidateUyaIso:
                    await HandleValidateUyaIsoAsync(frame, writer, requestCancellation.Token, hostCancellation);
                    break;
                case BridgeOpcode.CreateDevelopmentIso:
                    await HandleCreateDevelopmentIsoAsync(frame, writer, requestCancellation.Token, hostCancellation);
                    break;
                case BridgeOpcode.ImportUyaAssets:
                    await HandleImportUyaAssetsAsync(
                        frame, writer, handshake.SdkRevision, requestCancellation.Token, hostCancellation);
                    break;
                case BridgeOpcode.ListUyaProjectLevels:
                case BridgeOpcode.CreateUyaProject:
                case BridgeOpcode.InspectForgeProject:
                case BridgeOpcode.RenameForgeProject:
                case BridgeOpcode.PreflightUyaProject:
                case BridgeOpcode.RestoreForgeProjectRecovery:
                case BridgeOpcode.MigrateForgeProject:
                case BridgeOpcode.RepairForgeProjectAssets:
                case BridgeOpcode.PreviewCatalogGarbageCollection:
                case BridgeOpcode.CollectCatalogGarbage:
                    await writer.WriteAsync(new(
                        BridgeMessageKind.Result, frame.Opcode, BridgeErrorCode.None, frame.RequestId,
                        await ProjectBridgeHandlers.HandleAsync(
                            frame, handshake.SdkRevision,
                            CreateProgressReporter(frame, writer, hostCancellation),
                            requestCancellation.Token)), hostCancellation);
                    break;
                case BridgeOpcode.OpenEditorProject:
                case BridgeOpcode.CloseEditorProject:
                case BridgeOpcode.QueryEditor:
                case BridgeOpcode.ExecuteEditorCommand:
                case BridgeOpcode.SaveEditorProject:
                case BridgeOpcode.ReadEditorEvents:
                    await writer.WriteAsync(new(
                        BridgeMessageKind.Result, frame.Opcode, BridgeErrorCode.None, frame.RequestId,
                        await EditorBridgeHandlers.HandleAsync(frame, editor, requestCancellation.Token)), hostCancellation);
                    break;
                case BridgeOpcode.PrepareUyaRenderPackage:
                    await writer.WriteAsync(new(
                        BridgeMessageKind.Result, frame.Opcode, BridgeErrorCode.None, frame.RequestId,
                        await RenderBridgeHandlers.PrepareUyaAsync(
                            frame.Payload,
                            handshake.SdkRevision,
                            CreateProgressReporter(frame, writer, hostCancellation),
                            requestCancellation.Token)), hostCancellation);
                    break;
                case BridgeOpcode.BuildAndPatchUyaProject:
                    await writer.WriteAsync(new(
                        BridgeMessageKind.Result, frame.Opcode, BridgeErrorCode.None, frame.RequestId,
                        await ProjectBridgeHandlers.BuildAndPatchAsync(
                            frame.Payload,
                            handshake.HostVersion,
                            handshake.SdkRevision,
                            CreateBuildProgressReporter(frame, writer, hostCancellation),
                            requestCancellation.Token)), hostCancellation);
                    break;
                case BridgeOpcode.GetUyaBuildPlan:
                    await writer.WriteAsync(new(
                        BridgeMessageKind.Result, frame.Opcode, BridgeErrorCode.None, frame.RequestId,
                        await ProjectBridgeHandlers.GetBuildPlanAsync(
                            frame.Payload,
                            handshake.HostVersion,
                            handshake.SdkRevision,
                            requestCancellation.Token)), hostCancellation);
                    break;
                default:
                    await WriteErrorAsync(writer, frame.RequestId, BridgeErrorCode.UnknownOpcode,
                        $"Unsupported opcode: {frame.Opcode}", hostCancellation);
                    break;
            }
        }
        catch (OperationCanceledException exception) when (requestCancellation.IsCancellationRequested)
        {
            await WriteErrorAsync(writer, frame.RequestId, BridgeErrorCode.Cancelled,
                string.IsNullOrWhiteSpace(exception.Message) ? "Operation cancelled" : exception.Message,
                hostCancellation);
        }
        catch (BridgeProtocolException exception)
        {
            await WriteErrorAsync(writer, frame.RequestId, exception.Code, exception.Message, hostCancellation);
        }
        catch (ArgumentException exception)
        {
            await WriteErrorAsync(writer, frame.RequestId, BridgeErrorCode.InvalidInput, exception.Message, hostCancellation);
        }
        catch (InvalidDataException exception)
        {
            await WriteErrorAsync(writer, frame.RequestId, BridgeErrorCode.InvalidInput, exception.Message, hostCancellation);
        }
        catch (FormatException exception)
        {
            await WriteErrorAsync(writer, frame.RequestId, BridgeErrorCode.InvalidInput, exception.Message, hostCancellation);
        }
        catch (InvalidOperationException exception)
        {
            await WriteErrorAsync(writer, frame.RequestId, BridgeErrorCode.Conflict, exception.Message, hostCancellation);
        }
        catch (IOException exception)
        {
            var code = exception.Message.StartsWith("Not enough free space", StringComparison.Ordinal)
                ? BridgeErrorCode.InsufficientSpace
                : BridgeErrorCode.Conflict;
            await WriteErrorAsync(writer, frame.RequestId, code, exception.Message, hostCancellation);
        }
        catch (UnauthorizedAccessException exception)
        {
            await WriteErrorAsync(writer, frame.RequestId, BridgeErrorCode.Conflict, exception.Message, hostCancellation);
        }
        catch (Exception exception)
        {
            await diagnostics.WriteLineAsync($"Request {frame.RequestId} failed: {exception}");
            await WriteErrorAsync(writer, frame.RequestId, BridgeErrorCode.InternalError,
                "Host operation failed", hostCancellation);
        }
        finally
        {
            requests.TryRemove(frame.RequestId, out _);
            requestCancellation.Dispose();
        }
    }

    private static async Task HandleEchoAsync(
        BridgeFrame frame,
        FrameWriter writer,
        CancellationToken requestCancellation,
        CancellationToken hostCancellation)
    {
        var request = BridgePayloadCodec.DecodeEchoRequest(frame.Payload);
        if (request.DelayMs > 0)
        {
            const uint steps = 10;
            var delay = TimeSpan.FromMilliseconds(request.DelayMs / (double)steps);
            for (uint completed = 1; completed <= steps; completed++)
            {
                await Task.Delay(delay, requestCancellation);
                await writer.WriteAsync(new(
                    BridgeMessageKind.Progress,
                    BridgeOpcode.Echo,
                    BridgeErrorCode.None,
                    frame.RequestId,
                    BridgePayloadCodec.EncodeProgress(completed, steps)), hostCancellation);
            }
        }

        requestCancellation.ThrowIfCancellationRequested();
        await writer.WriteAsync(new(
            BridgeMessageKind.Result,
            BridgeOpcode.Echo,
            BridgeErrorCode.None,
            frame.RequestId,
            BridgePayloadCodec.EncodeText(request.Message)), hostCancellation);
    }

    private static async Task HandleValidateUyaIsoAsync(
        BridgeFrame frame,
        FrameWriter writer,
        CancellationToken requestCancellation,
        CancellationToken hostCancellation)
    {
        var identity = await UyaIsoService.ValidateAsync(
            BridgePayloadCodec.DecodeText(frame.Payload),
            CreateProgressReporter(frame, writer, hostCancellation),
            requestCancellation);
        await writer.WriteAsync(new(
            BridgeMessageKind.Result,
            frame.Opcode,
            BridgeErrorCode.None,
            frame.RequestId,
            BridgePayloadCodec.EncodeUyaIsoValidation(new(
                identity.IsSupported,
                identity.Game,
                identity.Region,
                identity.Revision,
                identity.Serial,
                checked((ulong)identity.Size),
                identity.Fingerprint,
                identity.Diagnostic))), hostCancellation);
    }

    private static async Task HandleCreateDevelopmentIsoAsync(
        BridgeFrame frame,
        FrameWriter writer,
        CancellationToken requestCancellation,
        CancellationToken hostCancellation)
    {
        var request = BridgePayloadCodec.DecodeDevelopmentIsoRequest(frame.Payload);
        var result = await UyaIsoService.CreateDevelopmentCopyAsync(
            request.SourcePath,
            request.TargetPath,
            request.Fingerprint,
            request.Overwrite,
            CreateProgressReporter(frame, writer, hostCancellation),
            requestCancellation);
        await writer.WriteAsync(new(
            BridgeMessageKind.Result,
            frame.Opcode,
            BridgeErrorCode.None,
            frame.RequestId,
            BridgePayloadCodec.EncodeDevelopmentIso(new(
                result.Path,
                checked((ulong)result.Size),
                result.Fingerprint))), hostCancellation);
    }

    private static async Task HandleImportUyaAssetsAsync(
        BridgeFrame frame,
        FrameWriter writer,
        string sdkRevision,
        CancellationToken requestCancellation,
        CancellationToken hostCancellation)
    {
        var request = BridgePayloadCodec.DecodeUyaAssetImportRequest(frame.Payload);
        var result = await UyaAssetImportService.ImportAsync(
            new(
                request.SourceIsoPath,
                request.CatalogRootPath,
                request.Fingerprint,
                request.Revision,
                $"forge-uya-v2+{sdkRevision}",
                request.Force),
            CreateProgressReporter(frame, writer, hostCancellation),
            requestCancellation);
        await writer.WriteAsync(new(
            BridgeMessageKind.Result,
            frame.Opcode,
            BridgeErrorCode.None,
            frame.RequestId,
            BridgePayloadCodec.EncodeUyaAssetImportResult(new(
                checked((uint)result.CompletedLevels),
                checked((uint)result.TotalLevels),
                checked((uint)result.AssetAppearances),
                checked((uint)result.UniqueAssets),
                checked((uint)result.FailedAssets),
                result.Resumed))), hostCancellation);
    }

    private static Func<IsoProgress, ValueTask> CreateProgressReporter(
        BridgeFrame frame,
        FrameWriter writer,
        CancellationToken hostCancellation)
    {
        uint last = 0;
        return async progress =>
        {
            var completed = progress.Total <= 0
                ? 0u
                : checked((uint)Math.Min(10_000, progress.Completed * 10_000 / progress.Total));
            if (completed < 10_000 && completed - last < 25) return;
            last = completed;
            await writer.WriteAsync(new(
                BridgeMessageKind.Progress,
                frame.Opcode,
                BridgeErrorCode.None,
                frame.RequestId,
                BridgePayloadCodec.EncodeProgress(completed, 10_000)), hostCancellation);
        };
    }

    private static Func<UyaBuildPatchProgress, ValueTask> CreateBuildProgressReporter(
        BridgeFrame frame,
        FrameWriter writer,
        CancellationToken hostCancellation)
    {
        UyaBuildPatchPhase? lastPhase = null;
        long lastScaled = -1;
        return async progress =>
        {
            var scaled = progress.Total <= 0
                ? 0
                : Math.Min(10_000, progress.Completed * 10_000 / progress.Total);
            if (progress.Phase == lastPhase && scaled < 10_000 && scaled - lastScaled < 25) return;
            lastPhase = progress.Phase;
            lastScaled = scaled;
            await writer.WriteAsync(new(
                BridgeMessageKind.Progress,
                frame.Opcode,
                BridgeErrorCode.None,
                frame.RequestId,
                BuildPayloadCodec.EncodeProgress(new(
                    progress.Phase.ToString(),
                    checked((ulong)progress.Completed),
                    checked((ulong)progress.Total),
                    progress.Message))), hostCancellation);
        };
    }

    private static Task WriteErrorAsync(
        FrameWriter writer,
        uint requestId,
        BridgeErrorCode code,
        string message,
        CancellationToken cancellationToken) => writer.WriteAsync(new(
            BridgeMessageKind.Error,
            BridgeOpcode.Control,
            code,
            requestId,
            BridgeErrorPayload.Encode(message)), cancellationToken);

    private sealed class FrameWriter(Stream output)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task WriteAsync(BridgeFrame frame, CancellationToken cancellationToken)
        {
            var bytes = BridgeFrameCodec.Encode(frame);
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await output.WriteAsync(bytes, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}

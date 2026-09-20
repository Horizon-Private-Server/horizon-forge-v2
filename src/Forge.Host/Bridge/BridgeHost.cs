using System.Collections.Concurrent;

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

                    var task = HandleRequestAsync(frame, writer, requests, requestCancellation, diagnostics, cancellationToken);
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
        CancellationToken hostCancellation)
    {
        try
        {
            switch (frame.Opcode)
            {
                case BridgeOpcode.Echo:
                    await HandleEchoAsync(frame, writer, requestCancellation.Token, hostCancellation);
                    break;
                default:
                    await WriteErrorAsync(writer, frame.RequestId, BridgeErrorCode.UnknownOpcode,
                        $"Unsupported opcode: {frame.Opcode}", hostCancellation);
                    break;
            }
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            await WriteErrorAsync(writer, frame.RequestId, BridgeErrorCode.Cancelled,
                "Operation cancelled", hostCancellation);
        }
        catch (BridgeProtocolException exception)
        {
            await WriteErrorAsync(writer, frame.RequestId, exception.Code, exception.Message, hostCancellation);
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

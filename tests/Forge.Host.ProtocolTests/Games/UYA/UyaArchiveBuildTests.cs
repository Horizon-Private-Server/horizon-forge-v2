using Forge.Host.Games.UYA;
using System.Buffers.Binary;
using Forge.Host.Domain;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.Wad.Models;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaArchiveBuildTests
{
    public static async Task RunAsync()
    {
        var source = new byte[UyaLevelConstants.SectorSize];
        BinaryPrimitives.WriteInt32LittleEndian(source, UyaLevelConstants.LevelWadHeaderSize);

        var result = await Task.Run(() => LevelArchiveBuilder.Build(
            GameId.UYA,
            source,
            options: new() { RequireSourceEquality = true }));
        Equal(true, result.Succeeded, "archive build succeeded");
        Equal(LevelArchiveBuilder.SchemaVersion, result.SchemaVersion, "archive schema");
        Equal(true, source.AsSpan().SequenceEqual(result.OutputBytes), "archive bytes");
        Equal(64, result.SourceSha256.Length, "source hash");
        Equal(result.SourceSha256, result.CompressedSha256, "output hash");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => Task.Run(
            () => LevelArchiveBuilder.Build(GameId.UYA, source, cancellationToken: cancellation.Token),
            cancellation.Token));
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }
}

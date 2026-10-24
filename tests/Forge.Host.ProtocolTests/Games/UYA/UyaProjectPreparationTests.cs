using Forge.Host.Games.UYA;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaProjectPreparationTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"forge-project-preparation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.iso");
            var bytes = UyaProjectTests.CreateIso();
            UyaIsoSetupTests.AddDiscIdentity(bytes);
            await File.WriteAllBytesAsync(source, bytes);
            var request = new UyaProjectCreationRequest(source, Path.Combine(root, "catalog"),
                Path.Combine(root, "project"), "Preparation", new string('a', 32), "1.00", 3, true);
            foreach (var failure in new[] { "identity", "progress", "cancellation" })
            {
                using var cancellation = new CancellationTokenSource();
                Exception? error = null;
                try
                {
                    await UyaProjectService.CreateAsync(request, _ =>
                    {
                        if (failure == "progress") throw new InvalidOperationException("Progress failed");
                        if (failure == "cancellation") cancellation.Cancel();
                        return ValueTask.CompletedTask;
                    }, cancellation.Token);
                }
                catch (Exception exception) { error = exception; }
                if (failure == "identity" && error is not InvalidDataException
                    || failure == "progress" && error is not InvalidOperationException
                    || failure == "cancellation" && error is not OperationCanceledException)
                    throw new InvalidOperationException($"Unexpected {failure} result.", error);
                if (Directory.Exists(request.ProjectPath) || Directory.Exists(request.CatalogRootPath))
                    throw new InvalidOperationException("Unverified preparation published project or catalog state.");
                using var reopened = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

using Forge.Host.Games.UYA;
using System.Runtime.InteropServices;

namespace Forge.Host.Bridge;

internal static class OcclusionWorkerCommand
{
    internal static async Task<int> RunAsync(string[] args, string hostVersion, string sdkRevision)
    {
        using var cancellation = new CancellationTokenSource();
        using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        { context.Cancel = true; cancellation.Cancel(); });
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try
        {
            if (args.Length is 5 or 6 or 7 && args[0] == "--occlusion-prepare")
            {
                var samplePullIn = 2;
                if (args.Length >= 6 && (!int.TryParse(args[5], out samplePullIn) || samplePullIn is < 0 or > 5))
                    throw new ArgumentException("Occlusion sample pull-in must be a whole number from 0 to 5.");
                var pixelEdgeAllowance = false;
                if (args.Length == 7 && !bool.TryParse(args[6], out pixelEdgeAllowance))
                    throw new ArgumentException("Occlusion pixel edge allowance must be true or false.");
                await UyaOcclusionBakeService.PrepareAsync(args[1], args[2], args[3], args[4], hostVersion, sdkRevision,
                    cancellation.Token, samplePullIn, pixelEdgeAllowance);
            }
            else if (args is ["--occlusion-visibility", var project, var iso, var output])
                await UyaOcclusionVisibilityService.ExportAsync(project, iso, output, cancellation.Token);
            else if (args is ["--occlusion-commit", var root, var job])
            {
                await UyaOcclusionBakeService.CommitAsync(root, job, cancellation.Token);
            }
            else throw new ArgumentException("Invalid occlusion worker arguments.");
            return 0;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Occlusion rebuild cancelled."); return 130; }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }
}

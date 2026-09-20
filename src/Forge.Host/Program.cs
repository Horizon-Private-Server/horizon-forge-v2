using System.Reflection;
using Forge.Host.Bridge;
using RatchetPs2.Sdk;

var assembly = Assembly.GetExecutingAssembly();
var revision = assembly
    .GetCustomAttributes<AssemblyMetadataAttribute>()
    .Single(attribute => attribute.Key == "RatchetSdkRevision")
    .Value ?? "unknown";
var hostVersion = assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion ?? "unknown";
var sdkAssembly = typeof(FrontendMapPackageBuilder).Assembly.GetName().Name;

Console.Error.WriteLine($"Horizon Forge host is ready with {sdkAssembly} at {revision}.");

return await BridgeHost.RunAsync(
    Console.OpenStandardInput(),
    Console.OpenStandardOutput(),
    Console.Error,
    new(
        hostVersion,
        revision,
        ["UYA"],
        [
            "bridge.echo", "bridge.progress", "bridge.cancellation", "uya.iso.validate", "uya.iso.copy",
            "uya.assets.import", "uya.assets.import.mobys", "uya.assets.import.ties", "uya.assets.import.shrubs",
            "uya.projects.base.mobys", "projects.inspect", "projects.rename"
        ]));

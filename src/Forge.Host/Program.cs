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
            "uya.projects.base.instances", "projects.inspect", "projects.rename", "projects.recovery", "projects.migrate",
            "projects.assets.repair", "assets.catalog.gc", "editor.runtime", "editor.commands",
            "editor.events", "editor.autosave", "uya.render.terrain",
            "uya.bake.world.target-native", "uya.bake.sky.target-native",
            "uya.bake.tfrags.target-native", "uya.bake.collision.target-native",
            "uya.bake.ties.target-native", "uya.bake.shrubs.target-native",
            "uya.bake.mobys.target-native", "uya.bake.gameplay.pvars-pass-through",
            "uya.bake.lighting.target-native", "uya.bake.validate", "uya.bake.incremental",
            "uya.build.level-wad", "uya.build.patch", "uya.build.plan", "uya.build.selective"
        ]));

namespace Forge.Host.Domain;

internal static partial class ForgeProjectValidation
{
    private static void ValidateOcclusion(
        string rootPath,
        ProjectOcclusionState state,
        IReadOnlyList<ProjectAttachedAsset> assets)
    {
        if (state.SchemaVersion != ProjectOcclusionSchema.CurrentVersion || state.SourceLevel < 0
            || state.SourcePayload is not { Kind: AssetKind.World })
            throw new InvalidDataException("Project occlusion state is invalid.");
        var asset = assets.SingleOrDefault(value => value.Id == state.SourcePayload.Id);
        if (asset is null || asset.Kind != AssetKind.World
            || asset.CanonicalFormatVersion != ProjectOcclusionSchema.SourcePayloadCanonicalFormatVersion)
            throw new InvalidDataException("Imported occlusion source payload is not attached to the project.");
        var value = asset.Id.ToString();
        if (!File.Exists(Path.Combine(rootPath, "assets", value[..2], $"{value}.blob")))
            throw new InvalidDataException("Imported occlusion source payload blob is missing.");
    }
}

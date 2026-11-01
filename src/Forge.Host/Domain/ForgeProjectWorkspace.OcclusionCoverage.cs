using Forge.Host.Domain.Occlusion;

namespace Forge.Host.Domain;

public sealed partial class ForgeProjectWorkspace
{
    public string OcclusionCoverageRecipeFingerprint => OcclusionCoverageRecipe.Fingerprint(Content.OcclusionCoverage);

    private ForgeProjectManifest? _visibilityManifest;
    private ForgeProjectContent? _visibilityContent;
    private string? _visibilityFingerprint;

    public string OcclusionVisibilityFingerprint
    {
        get
        {
            if (!ReferenceEquals(Manifest, _visibilityManifest) || !ReferenceEquals(Content, _visibilityContent))
            {
                // Hiding and locking affect editor presentation, never the baked scene.
                var entities = Content.Entities.Select(e => e with
                    { State = e.State?.Disabled == true ? new(Disabled: true) : null }).ToArray();
                // Coverage has its own semantic fingerprint; names and inactive volumes do not change the scene.
                _visibilityFingerprint = ForgeProjectPersistence.Fingerprint(Manifest, Content with
                    { Entities = entities, OcclusionCoverage = ProjectOcclusionCoverageState.PrimaryOnly });
                _visibilityManifest = Manifest;
                _visibilityContent = Content;
            }
            return _visibilityFingerprint!;
        }
    }

    public void SetOcclusionCoverage(ProjectOcclusionCoverageState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var copy = OcclusionCoverageRecipe.Copy(state);
        cancellationToken.ThrowIfCancellationRequested();
        Content = Content with { OcclusionCoverage = copy };
    }

    public void SetPrimaryOcclusionCoverageEnabled(bool enabled) =>
        SetOcclusionCoverage(Content.OcclusionCoverage with { PrimaryEnabled = enabled });
}

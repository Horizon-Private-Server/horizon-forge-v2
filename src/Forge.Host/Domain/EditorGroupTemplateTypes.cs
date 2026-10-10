namespace Forge.Host.Domain;

public enum EditorGroupTemplateCandidateDisposition : byte
{
    Included = 1,
    Ambiguous = 2,
}

public sealed record EditorGroupTemplateParameterDefinition(
    string Key,
    string Label,
    double DefaultValue,
    double Minimum,
    double Maximum,
    double Step);

public sealed record EditorGroupTemplateDefinition(
    string TemplateId,
    int TemplateVersion,
    string Label,
    string Description,
    ProjectEntityKind SeedKind,
    IReadOnlyList<EditorGroupTemplateParameterDefinition> Parameters);

public sealed record EditorGroupTemplateRequest(
    string TemplateId,
    int TemplateVersion,
    IReadOnlyList<ProjectGroupTemplateParameter> Parameters,
    GroupId? GroupId = null,
    EntityId? SeedEntityId = null);

public sealed record EditorGroupTemplateCandidate(
    EntityId EntityId,
    EditorGroupTemplateCandidateDisposition Disposition,
    IReadOnlyList<string> Evidence);

public sealed record EditorGroupTemplateEvaluation(
    string SuggestedName,
    IReadOnlyList<EditorGroupTemplateCandidate> Candidates,
    IReadOnlyList<EditorDiagnostic> Diagnostics);

public delegate Task<EditorGroupTemplateEvaluation> EditorGroupTemplateEvaluator(
    ForgeProjectWorkspace workspace,
    EditorGroupTemplateQuery query,
    EditorGroupTemplateRequest request,
    CancellationToken cancellationToken);

public sealed record EditorGroupTemplatePreview(
    string Token,
    string TemplateId,
    int TemplateVersion,
    IReadOnlyList<ProjectGroupTemplateParameter> Parameters,
    GroupId? GroupId,
    EntityId? SeedEntityId,
    string SuggestedName,
    int CandidateCount,
    int AmbiguousCandidateCount,
    IReadOnlyList<EditorGroupTemplateCandidate> Candidates,
    IReadOnlyList<EntityId> Members,
    IReadOnlyList<EntityId> AddedMembers,
    IReadOnlyList<EntityId> RemovedMembers,
    IReadOnlyList<EntityId> ManualAdditions,
    IReadOnlyList<EntityId> ManualRemovals,
    IReadOnlyList<EditorDiagnostic> Diagnostics);

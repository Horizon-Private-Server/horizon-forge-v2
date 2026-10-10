namespace Forge.Host.Domain;

public sealed partial class EditorRuntime
{
    private const int MaximumTemplateEvidencePerCandidate = 32;
    private const int MaximumTemplatePreviewCandidates = 250;
    private Dictionary<string, CachedGroupTemplatePreview> _groupTemplatePreviews = [];

    private sealed record CachedGroupTemplatePreview(
        EntityId ProjectId,
        long ProjectVersion,
        string GroupName,
        IReadOnlyList<EntityId> FinalMembers,
        ProjectGroupTemplateProvenance Provenance,
        GroupId? GroupId);

    public async Task<EditorGroupTemplatePreview> PreviewGroupTemplateAsync(
        EditorGroupTemplateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var workspace = RequireWorkspace();
            if (_groupTemplateEvaluator is null)
                throw new InvalidOperationException("Group template evaluation is unavailable.");
            var normalizedRequest = NormalizeTemplateRequest(request, workspace);
            var inputFingerprint = workspace.CurrentFingerprint;
            var evaluation = await _groupTemplateEvaluator(
                workspace,
                new EditorGroupTemplateQuery(workspace.Content),
                normalizedRequest,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = NormalizeTemplateEvaluation(evaluation, workspace);
            var generatedMembers = normalized.Candidates
                .Where(candidate => candidate.Disposition == EditorGroupTemplateCandidateDisposition.Included)
                .Select(candidate => candidate.EntityId)
                .ToArray();
            var existing = normalizedRequest.GroupId is { } groupId
                ? workspace.Content.Groups.Single(group => group.GroupId == groupId)
                : null;
            var previousGenerated = existing?.Template?.GeneratedMembers ?? [];
            var currentMembers = existing?.Members ?? [];
            var previousGeneratedSet = previousGenerated.ToHashSet();
            var currentSet = currentMembers.ToHashSet();
            var manualAdditions = currentMembers.Where(member => !previousGeneratedSet.Contains(member)).ToArray();
            var manualRemovals = previousGenerated.Where(member => !currentSet.Contains(member)).ToArray();
            var manualAdditionSet = manualAdditions.ToHashSet();
            var manualRemovalSet = manualRemovals.ToHashSet();
            var finalMembers = generatedMembers.Where(member => !manualRemovalSet.Contains(member)
                    && !manualAdditionSet.Contains(member))
                .Concat(manualAdditions)
                .Distinct()
                .ToArray();
            var generatedBaseline = existing is null
                ? generatedMembers
                : generatedMembers.Where(member => !manualAdditionSet.Contains(member))
                    .Concat(manualRemovals)
                    .Distinct()
                    .OrderBy(member => member.ToString(), StringComparer.Ordinal)
                    .ToArray();
            var finalSet = finalMembers.ToHashSet();
            var added = finalMembers.Where(member => !currentSet.Contains(member)).ToArray();
            var removed = currentMembers.Where(member => !finalSet.Contains(member)).ToArray();
            var parameters = normalizedRequest.Parameters.ToArray();
            var provenance = new ProjectGroupTemplateProvenance(
                normalizedRequest.TemplateId,
                normalizedRequest.TemplateVersion,
                parameters,
                inputFingerprint,
                generatedBaseline,
                normalizedRequest.SeedEntityId);
            var previewCandidates = normalized.Candidates.Count <= MaximumTemplatePreviewCandidates
                ? normalized.Candidates
                : normalized.Candidates
                    .OrderBy(candidate => candidate.Disposition
                        == EditorGroupTemplateCandidateDisposition.Ambiguous ? 0 : 1)
                    .ThenBy(candidate => candidate.EntityId.ToString(), StringComparer.Ordinal)
                    .Take(MaximumTemplatePreviewCandidates)
                    .ToArray();
            var token = Guid.NewGuid().ToString("D");
            var preview = new EditorGroupTemplatePreview(
                token,
                normalizedRequest.TemplateId,
                normalizedRequest.TemplateVersion,
                parameters,
                normalizedRequest.GroupId,
                normalizedRequest.SeedEntityId,
                normalized.SuggestedName,
                normalized.Candidates.Count,
                normalized.Candidates.Count(candidate => candidate.Disposition
                    == EditorGroupTemplateCandidateDisposition.Ambiguous),
                previewCandidates,
                finalMembers,
                added,
                removed,
                manualAdditions,
                manualRemovals,
                normalized.Diagnostics);
            _groupTemplatePreviews = new(StringComparer.Ordinal)
            {
                [token] = new(
                    workspace.Manifest.ProjectId,
                    _projectVersion,
                    existing?.Name ?? normalized.SuggestedName,
                    finalMembers,
                    provenance,
                    normalizedRequest.GroupId),
            };
            return preview;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EditorSnapshot> ApplyGroupTemplatePreviewAsync(
        string commandId,
        string token,
        CancellationToken cancellationToken = default)
    {
        ValidateCommandId(commandId);
        if (!Guid.TryParseExact(token, "D", out _))
            throw new ArgumentException("Group template preview token is invalid.", nameof(token));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var workspace = RequireWorkspace();
            if (!_groupTemplatePreviews.TryGetValue(token, out var cached)
                || cached.ProjectId != workspace.Manifest.ProjectId)
                throw new InvalidOperationException("Group template preview is stale; generate it again.");
            if (cached.ProjectVersion != _projectVersion)
                throw new InvalidOperationException("The project changed after this preview; generate it again.");
            cancellationToken.ThrowIfCancellationRequested();

            var before = workspace.CaptureState();
            ProjectGroup group;
            if (cached.GroupId is { } groupId)
            {
                var current = workspace.Content.Groups.SingleOrDefault(value => value.GroupId == groupId);
                if (current?.Template is null)
                    throw new InvalidOperationException("The generated group is no longer available.");
                group = workspace.RegenerateTemplateGroup(
                    groupId, cached.FinalMembers, cached.Provenance);
            }
            else
            {
                group = workspace.CreateTemplateGroup(
                    cached.GroupName, cached.FinalMembers, cached.Provenance);
            }
            _projectVersion++;
            _history.Push(
                before,
                workspace.CaptureState(),
                _selection,
                _selection,
                cached.FinalMembers,
                512L + cached.FinalMembers.Count * 16L);
            AddEvent(EditorEventKind.ProjectChanged, commandId, cached.FinalMembers,
                cached.GroupId is null
                    ? $"Generated group {group.Name} created"
                    : $"Generated group {group.Name} regenerated");
            _groupTemplatePreviews = [];
            ScheduleAutosave();
            return Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    private EditorGroupTemplateRequest NormalizeTemplateRequest(
        EditorGroupTemplateRequest request,
        ForgeProjectWorkspace workspace)
    {
        if (!ForgeProjectValidation.ValidTemplateIdentifier(request.TemplateId) || request.TemplateVersion < 1)
            throw new ArgumentException("Group template identity is invalid.", nameof(request));
        var definition = _groupTemplates.SingleOrDefault(value => value.TemplateId == request.TemplateId)
            ?? throw new ArgumentException("Group template is unavailable.", nameof(request));
        if (request.TemplateVersion != definition.TemplateVersion)
            throw new ArgumentException("Group template version is unavailable.", nameof(request));
        if (request.Parameters is null || request.Parameters.Count > ProjectGroupSchema.MaximumTemplateParameters
            || request.Parameters.Any(parameter => parameter is null
                || !ForgeProjectValidation.ValidTemplateIdentifier(parameter.Key)
                || !double.IsFinite(parameter.Value))
            || request.Parameters.Select(parameter => parameter.Key).Distinct(StringComparer.Ordinal).Count()
                != request.Parameters.Count)
            throw new ArgumentException("Group template parameters are invalid.", nameof(request));
        var seedEntityId = request.SeedEntityId;
        if (request.GroupId is { } groupId)
        {
            var group = workspace.Content.Groups.SingleOrDefault(value => value.GroupId == groupId);
            if (group?.Template is null || group.Template.TemplateId != request.TemplateId)
                throw new ArgumentException("Group template regeneration target is invalid.", nameof(request));
            seedEntityId ??= group.Template.SeedEntityId;
            if (group.Template.SeedEntityId != seedEntityId)
                throw new ArgumentException("Group template regeneration seed cannot be changed.", nameof(request));
        }
        var seed = seedEntityId is { } entityId
            ? workspace.Content.Entities.SingleOrDefault(entity => entity.EntityId == entityId)
            : null;
        if (seed is null || ProjectReferences.KindOf(seed) != definition.SeedKind)
            throw new ArgumentException($"Group template requires one {definition.SeedKind} seed entity.", nameof(request));
        var supplied = request.Parameters.ToDictionary(parameter => parameter.Key, StringComparer.Ordinal);
        if (definition.Parameters.Any(parameter => !supplied.TryGetValue(parameter.Key, out var value)
                || value.Value < parameter.Minimum || value.Value > parameter.Maximum)
            || supplied.Keys.Any(key => definition.Parameters.All(parameter => parameter.Key != key)))
            throw new ArgumentException("Group template parameters do not match the template definition.", nameof(request));
        return request with
        {
            Parameters = request.Parameters.OrderBy(parameter => parameter.Key, StringComparer.Ordinal).ToArray(),
            SeedEntityId = seedEntityId,
        };
    }

    private static IReadOnlyList<EditorGroupTemplateDefinition> NormalizeGroupTemplateDefinitions(
        IReadOnlyList<EditorGroupTemplateDefinition> definitions)
    {
        if (definitions.Count > ProjectGroupSchema.MaximumTemplateDefinitions
            || definitions.Any(definition => definition is null
                || !ForgeProjectValidation.ValidTemplateIdentifier(definition.TemplateId)
                || definition.TemplateVersion < 1
                || string.IsNullOrWhiteSpace(definition.Label) || definition.Label.Length > 256
                || string.IsNullOrWhiteSpace(definition.Description) || definition.Description.Length > 1_024
                || !Enum.IsDefined(definition.SeedKind)
                || definition.Parameters is null
                || definition.Parameters.Count > ProjectGroupSchema.MaximumTemplateParameters
                || definition.Parameters.Any(parameter => parameter is null
                    || !ForgeProjectValidation.ValidTemplateIdentifier(parameter.Key)
                    || string.IsNullOrWhiteSpace(parameter.Label) || parameter.Label.Length > 256
                    || !double.IsFinite(parameter.DefaultValue) || !double.IsFinite(parameter.Minimum)
                    || !double.IsFinite(parameter.Maximum) || !double.IsFinite(parameter.Step)
                    || parameter.Minimum > parameter.DefaultValue || parameter.DefaultValue > parameter.Maximum
                    || parameter.Step <= 0)
                || definition.Parameters.Select(parameter => parameter.Key).Distinct(StringComparer.Ordinal).Count()
                    != definition.Parameters.Count)
            || definitions.Select(definition => definition.TemplateId).Distinct(StringComparer.Ordinal).Count()
                != definitions.Count)
            throw new ArgumentException("Group template definitions are invalid.", nameof(definitions));
        return definitions.OrderBy(definition => definition.TemplateId, StringComparer.Ordinal).ToArray();
    }

    private static EditorGroupTemplateEvaluation NormalizeTemplateEvaluation(
        EditorGroupTemplateEvaluation evaluation,
        ForgeProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ForgeProjectValidation.ValidateText(evaluation.SuggestedName, nameof(evaluation.SuggestedName));
        if (evaluation.Candidates is null
            || evaluation.Candidates.Count > ProjectGroupSchema.MaximumMembersPerGroup
            || evaluation.Candidates.Any(candidate => candidate is null
                || !Enum.IsDefined(candidate.Disposition)
                || candidate.Evidence is null
                || candidate.Evidence.Count > MaximumTemplateEvidencePerCandidate)
            || evaluation.Candidates.Select(candidate => candidate.EntityId).Distinct().Count()
                != evaluation.Candidates.Count)
            throw new InvalidDataException("Group template candidates are invalid.");
        var known = workspace.Content.Entities.Select(entity => entity.EntityId).ToHashSet();
        if (evaluation.Candidates.Any(candidate => !known.Contains(candidate.EntityId)))
            throw new InvalidDataException("Group template candidates must reference project entities.");
        if (evaluation.Candidates.SelectMany(candidate => candidate.Evidence).Any(value =>
                string.IsNullOrWhiteSpace(value) || value.Length > 1_024))
            throw new InvalidDataException("Group template candidate evidence is invalid.");
        if (evaluation.Diagnostics is null || evaluation.Diagnostics.Count > MaxDiagnostics
            || evaluation.Diagnostics.Any(value => value is null
                || string.IsNullOrWhiteSpace(value.Code) || value.Code.Length > 128
                || !Enum.IsDefined(value.Severity)
                || string.IsNullOrWhiteSpace(value.Message) || value.Message.Length > 4_096))
            throw new InvalidDataException("Group template diagnostics are invalid.");
        return evaluation with
        {
            Candidates = evaluation.Candidates
                .OrderBy(candidate => candidate.EntityId.ToString(), StringComparer.Ordinal)
                .Select(candidate => candidate with
                {
                    Evidence = candidate.Evidence.Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal).ToArray(),
                }).ToArray(),
            Diagnostics = evaluation.Diagnostics.OrderBy(value => value.Code, StringComparer.Ordinal)
                .ThenBy(value => value.Message, StringComparer.Ordinal).ToArray(),
        };
    }
}

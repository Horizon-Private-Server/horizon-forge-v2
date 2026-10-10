using System.Globalization;
using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal static class UyaGroupTemplateService
{
    internal const string NodeTemplateId = "uya.node-cluster";
    internal const string BaseTemplateId = "uya.siege-base";
    private static readonly UyaGroupTemplateCatalog Catalog = UyaGroupTemplateDataset.Create();
    private static readonly IReadOnlyDictionary<string, UyaGroupTemplateRule> Rules = Catalog.Templates
        .ToDictionary(value => value.TemplateId, StringComparer.Ordinal);

    public static IReadOnlyList<EditorGroupTemplateDefinition> Definitions { get; } = Catalog.Templates
        .Select(value => new EditorGroupTemplateDefinition(
            value.TemplateId,
            value.TemplateVersion,
            value.Label,
            value.Description,
            value.SeedKind,
            value.Parameters))
        .ToArray();

    public static async Task<int> ApplyDefaultsAsync(
        ForgeProjectWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Manifest.Target.Game != "UYA")
            throw new InvalidOperationException("UYA group templates require a UYA project.");

        var query = new EditorGroupTemplateQuery(workspace.Content);
        var inputFingerprint = workspace.CurrentFingerprint;
        var existing = workspace.Content.Groups
            .Where(group => group.Template?.SeedEntityId is not null)
            .Select(group => (group.Template!.TemplateId, group.Template.SeedEntityId!.Value))
            .ToHashSet();
        var created = 0;
        foreach (var rule in Catalog.Templates.OrderBy(value => value.TemplateId, StringComparer.Ordinal))
        {
            var seedClass = UyaGroupTemplateDataset.ClassId(rule.SeedClass);
            foreach (var seed in query.OfKind(rule.SeedKind)
                         .Where(entity => entity.Source?.ClassId == seedClass))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!existing.Add((rule.TemplateId, seed.EntityId))) continue;
                var parameters = rule.Parameters
                    .Select(parameter => new ProjectGroupTemplateParameter(parameter.Key, parameter.DefaultValue))
                    .OrderBy(parameter => parameter.Key, StringComparer.Ordinal)
                    .ToArray();
                var request = new EditorGroupTemplateRequest(
                    rule.TemplateId, rule.TemplateVersion, parameters, SeedEntityId: seed.EntityId);
                var evaluation = await EvaluateAsync(workspace, query, request, cancellationToken);
                var members = evaluation.Candidates
                    .Where(candidate => candidate.Disposition == EditorGroupTemplateCandidateDisposition.Included)
                    .Select(candidate => candidate.EntityId)
                    .Distinct()
                    .OrderBy(entityId => entityId.ToString(), StringComparer.Ordinal)
                    .ToArray();
                workspace.CreateTemplateGroup(
                    evaluation.SuggestedName,
                    members,
                    new(rule.TemplateId, rule.TemplateVersion, parameters, inputFingerprint, members, seed.EntityId));
                created++;
            }
        }
        return created;
    }

    public static Task<EditorGroupTemplateEvaluation> EvaluateAsync(
        ForgeProjectWorkspace workspace,
        EditorGroupTemplateQuery query,
        EditorGroupTemplateRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (workspace.Manifest.Target.Game != "UYA")
            throw new InvalidOperationException("UYA group templates require a UYA project.");
        if (!Rules.TryGetValue(request.TemplateId, out var rule))
            throw new InvalidOperationException("The requested UYA group template is unavailable.");
        var seed = query.Find(request.SeedEntityId!.Value)
            ?? throw new InvalidOperationException("The group template seed no longer exists.");
        var seedClass = UyaGroupTemplateDataset.ClassId(rule.SeedClass);
        if (seed.Source?.ClassId != seedClass)
            throw new InvalidOperationException(
                $"{rule.Label} template requires OClass 0x{seedClass:X4} as its seed.");

        var candidates = new CandidateSet();
        candidates.Include(seed, $"Selected {rule.SeedLabel} OClass 0x{seedClass:X4}.");
        foreach (var referenceRule in rule.ReferenceRules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyReferenceRule(query, candidates, seed, referenceRule, cancellationToken);
        }
        foreach (var nearbyRule in rule.NearbyRules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddNearby(query, candidates, seed, seedClass, nearbyRule,
                Parameter(request, nearbyRule.RadiusParameter),
                Parameter(request, nearbyRule.AmbiguityParameter),
                cancellationToken);
        }
        if (rule.IncludeLinkedCollision)
        {
            foreach (var entityId in candidates.IncludedEntityIds.ToArray())
            foreach (var collision in query.LinkedCollision(entityId))
                candidates.Include(collision, "A selected template member links this collision component.");
        }
        return Task.FromResult(new EditorGroupTemplateEvaluation(
            SuggestedName(query, seed, rule), candidates.Build(), []));
    }

    private static void ApplyReferenceRule(
        EditorGroupTemplateQuery query,
        CandidateSet candidates,
        ProjectEntity seed,
        UyaGroupTemplateReferenceRule rule,
        CancellationToken cancellationToken)
    {
        ProjectEntity[] owners = rule.Scope == UyaGroupTemplateReferenceScope.Seed
            ? [seed]
            : candidates.IncludedEntityIds.Select(query.Find).OfType<ProjectEntity>().ToArray();
        var ownerClasses = rule.OwnerClasses.Select(UyaGroupTemplateDataset.ClassId).ToHashSet();
        foreach (var owner in owners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((rule.OwnerKinds.Count > 0 && !rule.OwnerKinds.Contains(ProjectReferences.KindOf(owner)))
                || (ownerClasses.Count > 0
                    && (owner.Source is null || !ownerClasses.Contains(owner.Source.ClassId)))) continue;
            var edges = rule.Direction == UyaGroupTemplateReferenceDirection.Outgoing
                ? query.Outgoing(owner.EntityId)
                : query.Incoming(owner.EntityId);
            foreach (var edge in edges.Where(edge => MatchesField(rule, edge.Reference.FieldKey)))
            {
                var entityId = rule.Direction == UyaGroupTemplateReferenceDirection.Outgoing
                    ? edge.Reference.EntityId
                    : edge.OwnerEntityId;
                if (entityId is not { } targetId || query.Find(targetId) is not { } target
                    || (rule.TargetKinds.Count > 0
                        && !rule.TargetKinds.Contains(ProjectReferences.KindOf(target)))) continue;
                var evidence = rule.Direction == UyaGroupTemplateReferenceDirection.Outgoing
                    ? $"{rule.EvidenceLabel}.{edge.Reference.FieldKey} references this entity."
                    : $"{edge.Reference.FieldKey} references {rule.EvidenceLabel}.";
                candidates.Include(target, evidence);
            }
        }
    }

    private static bool MatchesField(UyaGroupTemplateReferenceRule rule, string field) =>
        rule.Fields.Count == 0 && rule.FieldPrefixes.Count == 0
        || rule.Fields.Contains(field, StringComparer.Ordinal)
        || rule.FieldPrefixes.Any(prefix => field.StartsWith(prefix, StringComparison.Ordinal));

    private static void AddNearby(
        EditorGroupTemplateQuery query,
        CandidateSet candidates,
        ProjectEntity seed,
        int seedClass,
        UyaGroupTemplateNearbyRule rule,
        double radius,
        double ambiguityMargin,
        CancellationToken cancellationToken)
    {
        var classIds = rule.ClassIds.Select(UyaGroupTemplateDataset.ClassId).ToHashSet();
        var anchors = query.OfKind(ProjectReferences.KindOf(seed))
            .Where(entity => entity.Source?.ClassId == seedClass).ToArray();
        foreach (var entity in query.Nearby(
                     seed.Transform.Position, (float)radius, rule.EntityKind, cancellationToken)
                     .Where(entity => classIds.Count == 0
                         || entity.Source is { } source && classIds.Contains(source.ClassId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selectedDistance = Distance(seed, entity);
            var nearestOtherDistance = double.PositiveInfinity;
            foreach (var anchor in anchors)
            {
                if (anchor.EntityId == seed.EntityId) continue;
                nearestOtherDistance = Math.Min(nearestOtherDistance, Distance(anchor, entity));
            }
            var label = entity.Source is { } source && classIds.Count > 0
                ? $"{rule.EvidenceLabel} OClass 0x{source.ClassId:X4}"
                : rule.EvidenceLabel;
            var evidence = $"{label} is {Format(selectedDistance)} units from the selected anchor"
                + $" (limit {Format(radius)}).";
            if (nearestOtherDistance < selectedDistance
                || Math.Abs(nearestOtherDistance - selectedDistance) <= ambiguityMargin)
            {
                candidates.Ambiguous(entity, evidence,
                    $"Another matching anchor is {Format(nearestOtherDistance)} units away"
                    + $" (ambiguity margin {Format(ambiguityMargin)}).");
            }
            else candidates.Include(entity, evidence);
        }
    }

    private static string SuggestedName(
        EditorGroupTemplateQuery query,
        ProjectEntity seed,
        UyaGroupTemplateRule rule)
    {
        var incomingFields = query.Incoming(seed.EntityId)
            .Select(edge => edge.Reference.FieldKey).ToHashSet(StringComparer.Ordinal);
        var named = rule.NameRules.FirstOrDefault(value => incomingFields.Contains(value.IncomingField));
        return named?.Name ?? rule.SuggestedName.Replace(
            "{sourceIndex}", SourceIndex(seed).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static double Parameter(EditorGroupTemplateRequest request, string key) =>
        request.Parameters.Single(value => value.Key == key).Value;

    private static int SourceIndex(ProjectEntity entity) =>
        entity.Provenance?.SourceIndex ?? entity.Source?.SourceIndex ?? 0;

    private static double Distance(ProjectEntity first, ProjectEntity second)
    {
        var dx = (double)first.Transform.Position.X - second.Transform.Position.X;
        var dy = (double)first.Transform.Position.Y - second.Transform.Position.Y;
        var dz = (double)first.Transform.Position.Z - second.Transform.Position.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed class CandidateSet
    {
        private readonly Dictionary<EntityId, (EditorGroupTemplateCandidateDisposition Disposition, List<string> Evidence)>
            _values = [];

        public void Include(ProjectEntity entity, params string[] evidence) =>
            Add(entity.EntityId, EditorGroupTemplateCandidateDisposition.Included, evidence);

        public void Ambiguous(ProjectEntity entity, params string[] evidence) =>
            Add(entity.EntityId, EditorGroupTemplateCandidateDisposition.Ambiguous, evidence);

        public IEnumerable<EntityId> IncludedEntityIds => _values
            .Where(value => value.Value.Disposition == EditorGroupTemplateCandidateDisposition.Included)
            .Select(value => value.Key);

        public IReadOnlyList<EditorGroupTemplateCandidate> Build() => _values.Select(value => new
            EditorGroupTemplateCandidate(value.Key, value.Value.Disposition, value.Value.Evidence)).ToArray();

        private void Add(
            EntityId entityId,
            EditorGroupTemplateCandidateDisposition disposition,
            IReadOnlyList<string> evidence)
        {
            if (!_values.TryGetValue(entityId, out var current))
                current = (disposition, []);
            if (disposition == EditorGroupTemplateCandidateDisposition.Included)
                current.Disposition = disposition;
            current.Evidence.AddRange(evidence);
            _values[entityId] = current;
        }
    }
}

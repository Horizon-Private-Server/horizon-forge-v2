namespace Forge.Host.Domain;

public enum BakeDiagnosticSeverity
{
    Warning,
    Error,
}

public sealed record BakeDiagnostic(
    string Code,
    BakeDiagnosticSeverity Severity,
    BakeLayerId? Layer,
    EntityId? EntityId,
    AssetId? AssetId,
    string Cause,
    string CorrectiveAction);

public sealed record BakeValidationResult(
    BakePlan Plan,
    IReadOnlyList<BakeDiagnostic> Diagnostics,
    IReadOnlyList<string> UnacknowledgedWarnings)
{
    public bool CanBake => Diagnostics.All(value => value.Severity != BakeDiagnosticSeverity.Error)
        && UnacknowledgedWarnings.Count == 0;
}

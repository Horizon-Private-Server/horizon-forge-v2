using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal static class UyaGroupTemplateDataset
{
    private const int CurrentVersion = 1;
    private const int MaximumJsonBytes = 65_536;
    private const string ResourceName = "Forge.Host.Games.UYA.GroupTemplates.dataset.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) },
    };

    public static UyaGroupTemplateCatalog Create()
    {
        using var stream = typeof(UyaGroupTemplateDataset).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Embedded UYA group-template dataset is missing.");
        if (stream.Length > MaximumJsonBytes)
            throw new InvalidDataException("Embedded UYA group-template dataset exceeds the size limit.");
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        stream.ReadExactly(bytes);
        return Parse(bytes);
    }

    internal static UyaGroupTemplateCatalog Parse(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length is 0 or > MaximumJsonBytes)
            throw new InvalidDataException("UYA group-template JSON has an invalid size.");
        UyaGroupTemplateCatalog? catalog;
        try
        {
            catalog = JsonSerializer.Deserialize<UyaGroupTemplateCatalog>(utf8Json.Span, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("UYA group-template JSON is invalid.", exception);
        }
        Validate(catalog);
        return catalog!;
    }

    public static int ClassId(string value)
    {
        if (value is null || value.Length is < 3 or > 6
            || !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var result)
            || result is < 0 or > 0xFFFF)
            throw new InvalidDataException($"UYA group-template OClass '{value}' is invalid.");
        return result;
    }

    private static void Validate(UyaGroupTemplateCatalog? catalog)
    {
        if (catalog is null || catalog.SchemaVersion != CurrentVersion
            || catalog.Templates is null
            || catalog.Templates.Count is 0 or > ProjectGroupSchema.MaximumTemplateDefinitions
            || catalog.Templates.Any(InvalidTemplate)
            || catalog.Templates.Select(value => value.TemplateId)
                .Distinct(StringComparer.Ordinal).Count() != catalog.Templates.Count)
            throw new InvalidDataException("UYA group-template dataset is invalid.");
    }

    private static bool InvalidTemplate(UyaGroupTemplateRule value)
    {
        if (value is null
            || !ForgeProjectValidation.ValidTemplateIdentifier(value.TemplateId)
            || value.TemplateVersion < 1
            || InvalidText(value.Label, 256)
            || InvalidText(value.Description, 1_024)
            || !Enum.IsDefined(value.SeedKind)
            || InvalidClass(value.SeedClass)
            || InvalidText(value.SeedLabel, 256)
            || InvalidText(value.SuggestedName, 256)
            || value.Parameters is null
            || value.Parameters.Count > ProjectGroupSchema.MaximumTemplateParameters
            || value.Parameters.Any(InvalidParameter)
            || value.Parameters.Select(parameter => parameter.Key)
                .Distinct(StringComparer.Ordinal).Count() != value.Parameters.Count
            || value.ReferenceRules is null || value.ReferenceRules.Count > 32
            || value.ReferenceRules.Any(InvalidReferenceRule)
            || value.NearbyRules is null || value.NearbyRules.Count > 8
            || value.NearbyRules.Any(rule => InvalidNearbyRule(rule, value.Parameters))
            || value.NameRules is null || value.NameRules.Count > 16
            || value.NameRules.Any(rule => rule is null
                || InvalidText(rule.IncomingField, 128) || InvalidText(rule.Name, 256))) return true;
        return false;
    }

    private static bool InvalidParameter(EditorGroupTemplateParameterDefinition value) =>
        value is null
        || !ForgeProjectValidation.ValidTemplateIdentifier(value.Key)
        || InvalidText(value.Label, 256)
        || !double.IsFinite(value.DefaultValue) || !double.IsFinite(value.Minimum)
        || !double.IsFinite(value.Maximum) || !double.IsFinite(value.Step)
        || value.Minimum > value.DefaultValue || value.DefaultValue > value.Maximum
        || value.Step <= 0;

    private static bool InvalidReferenceRule(UyaGroupTemplateReferenceRule value) =>
        value is null
        || !Enum.IsDefined(value.Scope) || !Enum.IsDefined(value.Direction)
        || InvalidText(value.EvidenceLabel, 256)
        || InvalidKinds(value.OwnerKinds) || InvalidClasses(value.OwnerClasses)
        || InvalidStrings(value.Fields) || InvalidStrings(value.FieldPrefixes)
        || InvalidKinds(value.TargetKinds);

    private static bool InvalidNearbyRule(
        UyaGroupTemplateNearbyRule value,
        IReadOnlyList<EditorGroupTemplateParameterDefinition> parameters) =>
        value is null || !Enum.IsDefined(value.EntityKind)
        || InvalidClasses(value.ClassIds) || InvalidText(value.EvidenceLabel, 256)
        || parameters.All(parameter => parameter.Key != value.RadiusParameter)
        || parameters.All(parameter => parameter.Key != value.AmbiguityParameter);

    private static bool InvalidKinds(IReadOnlyList<ProjectEntityKind>? values) =>
        values is null || values.Count > 16 || values.Any(value => !Enum.IsDefined(value));

    private static bool InvalidClasses(IReadOnlyList<string>? values) =>
        values is null || values.Count > 128 || values.Any(InvalidClass);

    private static bool InvalidClass(string value)
    {
        try
        {
            _ = ClassId(value);
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    private static bool InvalidStrings(IReadOnlyList<string>? values) =>
        values is null || values.Count > 64 || values.Any(value => InvalidText(value, 128));

    private static bool InvalidText(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximumLength;
}

internal sealed record UyaGroupTemplateCatalog(
    int SchemaVersion,
    IReadOnlyList<UyaGroupTemplateRule> Templates);

internal sealed record UyaGroupTemplateRule(
    string TemplateId,
    int TemplateVersion,
    string Label,
    string Description,
    ProjectEntityKind SeedKind,
    string SeedClass,
    string SeedLabel,
    string SuggestedName,
    IReadOnlyList<UyaGroupTemplateNameRule> NameRules,
    IReadOnlyList<EditorGroupTemplateParameterDefinition> Parameters,
    IReadOnlyList<UyaGroupTemplateReferenceRule> ReferenceRules,
    IReadOnlyList<UyaGroupTemplateNearbyRule> NearbyRules,
    bool IncludeLinkedCollision);

internal enum UyaGroupTemplateReferenceScope : byte
{
    Seed = 1,
    IncludedMembers = 2,
}

internal enum UyaGroupTemplateReferenceDirection : byte
{
    Outgoing = 1,
    Incoming = 2,
}

internal sealed record UyaGroupTemplateReferenceRule(
    UyaGroupTemplateReferenceScope Scope,
    UyaGroupTemplateReferenceDirection Direction,
    string EvidenceLabel,
    IReadOnlyList<ProjectEntityKind> OwnerKinds,
    IReadOnlyList<string> OwnerClasses,
    IReadOnlyList<string> Fields,
    IReadOnlyList<string> FieldPrefixes,
    IReadOnlyList<ProjectEntityKind> TargetKinds);

internal sealed record UyaGroupTemplateNearbyRule(
    ProjectEntityKind EntityKind,
    IReadOnlyList<string> ClassIds,
    string EvidenceLabel,
    string RadiusParameter,
    string AmbiguityParameter);

internal sealed record UyaGroupTemplateNameRule(string IncomingField, string Name);

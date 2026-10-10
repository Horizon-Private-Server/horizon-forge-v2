using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Forge.Host.Domain;

public sealed class MobyDexCatalog
{
    public const string ProjectDatasetId = "project";
    private State _state;
    private readonly ConditionalWeakTable<ProjectMobyDexState, ProjectIndex> _projectIndexes = new();

    public MobyDexCatalog(MobyDexDataset builtIn)
    {
        _state = CreateState(builtIn);
    }

    public MobyDexDataset Dataset => Volatile.Read(ref _state).Dataset;

    public void Reload(MobyDexDataset replacement) => Volatile.Write(ref _state, CreateState(replacement));

    public MobyDexResolvedEntry? Resolve(ProjectMobyDexState? project, string game, int oClass)
    {
        ValidateKey(game, oClass);
        if (project is not null) ValidateProjectState(project);
        return ResolveValidated(project, game, oClass);
    }

    internal MobyDexResolvedEntry? ResolveValidated(ProjectMobyDexState? project, string game, int oClass)
    {
        if (project is not null
            && _projectIndexes.GetValue(project, CreateProjectIndex).Entries.TryGetValue((game, oClass), out var custom))
            return new(custom.Entry, MobyDexEntrySource.Project, ProjectDatasetId,
                project.SchemaVersion, custom.Entry.SchemaVersion, custom.Fingerprint);
        var state = Volatile.Read(ref _state);
        return state.Entries.TryGetValue((game, oClass), out var builtIn)
            ? new(builtIn.Entry, MobyDexEntrySource.BuiltIn,
                state.Dataset.Id, state.Dataset.Version, builtIn.Entry.SchemaVersion, builtIn.Fingerprint)
            : null;
    }

    private static State CreateState(MobyDexDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (string.IsNullOrWhiteSpace(dataset.Id) || dataset.Id.Length > 128 || dataset.Version < 1)
            throw new ArgumentException("MobyDex dataset identity is invalid.", nameof(dataset));
        ValidateEntries(dataset.Entries, "Built-in MobyDex dataset");
        var snapshot = dataset with { Entries = dataset.Entries.ToArray() };
        return new(snapshot, Index(snapshot.Entries));
    }

    private static ProjectIndex CreateProjectIndex(ProjectMobyDexState state) => new(Index(state.Entries));

    private static IReadOnlyDictionary<(string Game, int OClass), IndexedEntry> Index(
        IReadOnlyList<MobyDexEntry> entries) => entries.ToDictionary(
            entry => (entry.Game, entry.OClass),
            entry => new IndexedEntry(entry, Fingerprint(entry)));

    private static string Fingerprint(MobyDexEntry entry) =>
        Convert.ToHexString(SHA256.HashData(MobyDexSchema.Format(entry))).ToLowerInvariant();

    internal static void ValidateProjectState(ProjectMobyDexState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != ProjectMobyDexSchema.CurrentVersion)
            throw new InvalidDataException($"Unsupported project MobyDex schema {state.SchemaVersion}.");
        ValidateEntries(state.Entries, "Project MobyDex dataset");
    }

    private static void ValidateEntries(IReadOnlyList<MobyDexEntry>? entries, string description)
    {
        if (entries is null || entries.Count > ProjectMobyDexSchema.MaximumEntries)
            throw new InvalidDataException(
                $"{description} must contain at most {ProjectMobyDexSchema.MaximumEntries} entries.");
        var keys = new HashSet<(string Game, int OClass)>();
        foreach (var entry in entries)
        {
            if (entry is null) throw new InvalidDataException($"{description} cannot contain null entries.");
            try
            {
                if (MobyDexSchema.Format(entry).Length > MobyDexSchema.MaximumJsonBytes)
                    throw new InvalidDataException($"{description} entry {entry.Game}/{entry.OClass} exceeds the size limit.");
            }
            catch (MobyDexValidationException exception)
            {
                throw new InvalidDataException($"{description} entry is invalid: {exception.Message}", exception);
            }
            if (!keys.Add((entry.Game, entry.OClass)))
                throw new InvalidDataException($"{description} contains duplicate entry {entry.Game}/{entry.OClass}.");
        }
    }

    internal static void ValidateKey(string game, int oClass)
    {
        if (string.IsNullOrWhiteSpace(game) || game.Length is < 2 or > 16
            || game[0] is < 'A' or > 'Z'
            || game.Any(character => !(char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character)
                || character == '-'))
            || oClass is < 0 or > ushort.MaxValue)
            throw new ArgumentException("MobyDex lookup key is invalid.");
    }

    private sealed record State(
        MobyDexDataset Dataset,
        IReadOnlyDictionary<(string Game, int OClass), IndexedEntry> Entries);

    private sealed record ProjectIndex(IReadOnlyDictionary<(string Game, int OClass), IndexedEntry> Entries);

    private sealed record IndexedEntry(MobyDexEntry Entry, string Fingerprint);
}

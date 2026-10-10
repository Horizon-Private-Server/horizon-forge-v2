namespace Forge.Host.Domain;

public sealed partial class ForgeProjectWorkspace
{
    public MobyDexEntry ImportMobyDexEntry(ReadOnlyMemory<byte> utf8Json)
    {
        var entry = MobyDexSchema.Parse(utf8Json);
        var entries = (Content.MobyDex?.Entries ?? [])
            .Where(value => value.Game != entry.Game || value.OClass != entry.OClass)
            .Append(entry)
            .OrderBy(value => value.Game, StringComparer.Ordinal)
            .ThenBy(value => value.OClass)
            .ToArray();
        var state = new ProjectMobyDexState(ProjectMobyDexSchema.CurrentVersion, entries);
        MobyDexCatalog.ValidateProjectState(state);
        Content = Content with { MobyDex = state };
        return entry;
    }

    public void RemoveMobyDexEntry(string game, int oClass)
    {
        MobyDexCatalog.ValidateKey(game, oClass);
        var entries = Content.MobyDex?.Entries
            ?? throw new KeyNotFoundException($"Project MobyDex entry {game}/{oClass} does not exist.");
        var retained = entries.Where(entry => entry.Game != game || entry.OClass != oClass).ToArray();
        if (retained.Length == entries.Count)
            throw new KeyNotFoundException($"Project MobyDex entry {game}/{oClass} does not exist.");
        Content = Content with
        {
            MobyDex = retained.Length == 0
                ? null
                : new(ProjectMobyDexSchema.CurrentVersion, retained),
        };
    }

    public byte[] ExportMobyDexEntry(string game, int oClass)
    {
        MobyDexCatalog.ValidateKey(game, oClass);
        var entry = Content.MobyDex?.Entries.SingleOrDefault(value =>
            value.Game == game && value.OClass == oClass)
            ?? throw new KeyNotFoundException($"Project MobyDex entry {game}/{oClass} does not exist.");
        return MobyDexSchema.Format(entry);
    }

    public MobyDexResolvedEntry? ResolveMobyDexEntry(MobyDexCatalog catalog, string game, int oClass)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        MobyDexCatalog.ValidateKey(game, oClass);
        return catalog.ResolveValidated(Content.MobyDex, game, oClass);
    }
}

using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

public static class UyaMobyDexDataset
{
    private const string ResourceName = "Forge.Host.Games.UYA.MobyDex.dataset.json";

    public static MobyDexDataset Create()
    {
        using var stream = typeof(UyaMobyDexDataset).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Embedded UYA MobyDex dataset is missing.");
        if (stream.Length > MobyDexDatasetSchema.MaximumJsonBytes)
            throw new InvalidDataException("Embedded UYA MobyDex dataset exceeds the size limit.");
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        stream.ReadExactly(bytes);
        return Parse(bytes);
    }

    public static MobyDexDataset Parse(ReadOnlyMemory<byte> utf8Json)
    {
        var dataset = MobyDexDatasetSchema.Parse(utf8Json);
        if (dataset.Entries.Any(entry => entry.Game != "UYA"))
            throw new InvalidDataException("UYA MobyDex dataset contains an entry for another game.");
        return dataset;
    }
}

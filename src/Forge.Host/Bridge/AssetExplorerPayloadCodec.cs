namespace Forge.Host.Bridge;

internal static class AssetExplorerPayloadCodec
{
    private const uint MaxPageItems = 128;
    private const uint MaxMetadataItems = 1_024;
    private const uint MaxFacetItems = MaxPageItems * MaxMetadataItems;

    public static byte[] EncodeRequest(AssetExplorerRequestPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.CatalogRootPath);
        writer.WriteUInt32((uint)value.Category);
        WriteOptionalString(writer, value.Search);
        WriteOptionalString(writer, value.Game);
        WriteOptionalString(writer, value.Level);
        WriteOptionalString(writer, value.Region);
        WriteOptionalString(writer, value.Revision);
        writer.WriteStrings(value.Tags);
        WriteOptionalString(writer, value.Cursor);
        writer.WriteUInt32(value.Limit);
        writer.WriteString(value.TargetGame);
        writer.WriteString(value.TargetRegion);
        writer.WriteString(value.TargetRevision);
        return writer.ToArray();
    }

    public static AssetExplorerRequestPayload DecodeRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var catalogRootPath = reader.ReadString();
        var category = (AssetExplorerCategoryPayload)reader.ReadUInt32();
        var value = new AssetExplorerRequestPayload(
            catalogRootPath,
            category,
            ReadOptionalString(ref reader),
            ReadOptionalString(ref reader),
            ReadOptionalString(ref reader),
            ReadOptionalString(ref reader),
            ReadOptionalString(ref reader),
            reader.ReadStrings(),
            ReadOptionalString(ref reader),
            reader.ReadUInt32(),
            reader.ReadString(),
            reader.ReadString(),
            reader.ReadString());
        reader.Complete();
        if (!Enum.IsDefined(category)) PayloadFormat.Malformed("Unknown asset explorer category");
        return value;
    }

    public static byte[] EncodePage(AssetExplorerPagePayload value)
    {
        var writer = new PayloadWriter();
        if (value.Items.Count > MaxPageItems) PayloadFormat.Malformed("Asset explorer page exceeds item limit");
        writer.WriteUInt32((uint)value.Items.Count);
        foreach (var item in value.Items)
        {
            writer.WriteString(item.AssetId);
            writer.WriteUInt32((uint)item.Category);
            writer.WriteString(item.DisplayLabel);
            writer.WriteUInt32(item.CanonicalFormatVersion);
            writer.WriteUInt64(item.ByteSize);
            WriteStrings(writer, item.Aliases, MaxMetadataItems);
            WriteStrings(writer, item.Tags, MaxMetadataItems);
            if (item.Sources.Count > MaxMetadataItems) PayloadFormat.Malformed("Asset source list exceeds item limit");
            writer.WriteUInt32((uint)item.Sources.Count);
            foreach (var source in item.Sources)
            {
                writer.WriteString(source.Game);
                writer.WriteString(source.Region);
                writer.WriteString(source.Revision);
                writer.WriteString(source.Level);
                writer.WriteString(source.Archive);
                writer.WriteUInt32(source.SourceIndex);
            }
            if (item.ClassIds.Count > MaxMetadataItems) PayloadFormat.Malformed("Asset class list exceeds item limit");
            writer.WriteUInt32((uint)item.ClassIds.Count);
            foreach (var classId in item.ClassIds) writer.WriteUInt32(classId);
            writer.WriteString(item.PreviewState);
            writer.WriteBoolean(item.CanPlace);
            WriteOptionalString(writer, item.PlacementDisabledReason);
        }
        WriteStrings(writer, value.Facets.Games, MaxFacetItems);
        WriteStrings(writer, value.Facets.Levels, MaxFacetItems);
        WriteStrings(writer, value.Facets.Regions, MaxFacetItems);
        WriteStrings(writer, value.Facets.Revisions, MaxFacetItems);
        WriteStrings(writer, value.Facets.Tags, MaxFacetItems);
        WriteOptionalString(writer, value.NextCursor);
        return writer.ToArray();
    }

    public static AssetExplorerPagePayload DecodePage(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var itemCount = ReadCount(ref reader, MaxPageItems, "Asset explorer page");
        var items = new AssetExplorerItemPayload[itemCount];
        for (var index = 0; index < items.Length; index++)
        {
            var assetId = reader.ReadString();
            var category = (AssetExplorerCategoryPayload)reader.ReadUInt32();
            var label = reader.ReadString();
            var canonicalVersion = reader.ReadUInt32();
            var byteSize = reader.ReadUInt64();
            var aliases = ReadStrings(ref reader, MaxMetadataItems, "Asset alias list");
            var tags = ReadStrings(ref reader, MaxMetadataItems, "Asset tag list");
            var sourceCount = ReadCount(ref reader, MaxMetadataItems, "Asset source list");
            var sources = new AssetExplorerSourcePayload[sourceCount];
            for (var sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
                sources[sourceIndex] = new(
                    reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadString(),
                    reader.ReadString(), reader.ReadUInt32());
            var classIds = new uint[ReadCount(ref reader, MaxMetadataItems, "Asset class list")];
            for (var classIndex = 0; classIndex < classIds.Length; classIndex++) classIds[classIndex] = reader.ReadUInt32();
            items[index] = new(
                assetId, category, label, canonicalVersion, byteSize, aliases, tags, sources, classIds,
                reader.ReadString(), reader.ReadBoolean(), ReadOptionalString(ref reader));
            if (!Enum.IsDefined(category)) PayloadFormat.Malformed("Unknown asset explorer category");
        }
        var facets = new AssetExplorerFacetsPayload(
            ReadStrings(ref reader, MaxFacetItems, "Game facets"),
            ReadStrings(ref reader, MaxFacetItems, "Level facets"),
            ReadStrings(ref reader, MaxFacetItems, "Region facets"),
            ReadStrings(ref reader, MaxFacetItems, "Revision facets"),
            ReadStrings(ref reader, MaxFacetItems, "Tag facets"));
        var nextCursor = ReadOptionalString(ref reader);
        reader.Complete();
        return new(items, facets, nextCursor);
    }

    private static void WriteOptionalString(PayloadWriter writer, string? value) => writer.WriteString(value ?? string.Empty);

    private static string? ReadOptionalString(ref PayloadReader reader)
    {
        var value = reader.ReadString();
        return value.Length == 0 ? null : value;
    }

    private static void WriteStrings(PayloadWriter writer, IReadOnlyList<string> values, uint maximum)
    {
        if (values.Count > maximum) PayloadFormat.Malformed("List exceeds item limit");
        writer.WriteUInt32((uint)values.Count);
        foreach (var value in values) writer.WriteString(value);
    }

    private static string[] ReadStrings(ref PayloadReader reader, uint maximum, string name)
    {
        var values = new string[ReadCount(ref reader, maximum, name)];
        for (var index = 0; index < values.Length; index++) values[index] = reader.ReadString();
        return values;
    }

    private static int ReadCount(ref PayloadReader reader, uint maximum, string name)
    {
        var count = reader.ReadUInt32();
        if (count > maximum) PayloadFormat.Malformed($"{name} exceeds item limit");
        return checked((int)count);
    }
}

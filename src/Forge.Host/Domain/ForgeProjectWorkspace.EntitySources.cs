namespace Forge.Host.Domain;

public sealed partial class ForgeProjectWorkspace
{
    public void UpdateEntitySourceRecord(EntityId entityId, int expectedClassId, byte[] rawRecord)
        => UpdateEntitySourceRecords([(entityId, expectedClassId, rawRecord)]);

    public void UpdateEntitySourceRecords(
        IReadOnlyList<(EntityId EntityId, int ExpectedClassId, byte[] RawRecord)> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0 || updates.Select(value => value.EntityId).Distinct().Count() != updates.Count)
            throw new ArgumentException("Source record updates require unique entities.", nameof(updates));
        var indexes = updates.Select(value => FindEntityIndex(value.EntityId)).ToArray();
        for (var offset = 0; offset < updates.Count; offset++)
        {
            var update = updates[offset];
            ArgumentNullException.ThrowIfNull(update.RawRecord);
            var entity = Content.Entities[indexes[offset]];
            if (entity.Source is null || entity.Source.ClassId != update.ExpectedClassId)
                throw new InvalidOperationException("Entity source metadata no longer matches the property edit.");
        }
        var entities = Content.Entities.ToArray();
        for (var offset = 0; offset < updates.Count; offset++)
        {
            var update = updates[offset];
            var entity = entities[indexes[offset]];
            entities[indexes[offset]] = entity with
            {
                Source = entity.Source! with { RawRecord = update.RawRecord.ToArray() },
            };
        }
        Content = Content with { Entities = entities };
    }
}

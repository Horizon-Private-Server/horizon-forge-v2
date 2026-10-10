namespace Forge.Host.Domain;

internal static partial class ForgeProjectValidation
{
    private static void ValidateMobyPVars(ForgeProjectContent content)
    {
        if (content.MobyPVars is not { } state) return;
        if (state.SchemaVersion != ProjectMobyPVarSchema.CurrentVersion
            || state.SourceTableEntryCount < 0
            || state.SourceTableEntryCount > ProjectMobyPVarSchema.MaximumEntries
            || state.Entries is null
            || state.Entries.Count > ProjectMobyPVarSchema.MaximumEntries)
            throw new InvalidDataException("Project Moby PVar state is invalid.");
        if (state.Entries.Any(value => value is null)
            || state.Entries.Select(value => value.EntityId).Distinct().Count() != state.Entries.Count)
            throw new InvalidDataException("Project Moby PVar entries must have unique Entity IDs.");
        if (state.Entries.Where(value => value.SourceTableIndex is not null)
                .Select(value => value.SourceTableIndex).Distinct().Count()
            != state.Entries.Count(value => value.SourceTableIndex is not null))
            throw new InvalidDataException("Project Moby PVar source indexes must be unique.");

        var entities = content.Entities.ToDictionary(value => value.EntityId);
        foreach (var pvar in state.Entries)
        {
            if (!entities.TryGetValue(pvar.EntityId, out var entity) || entity.Layer != "mobys")
                throw new InvalidDataException($"Moby PVar owner {pvar.EntityId} does not exist.");
            if (pvar.SourceTableIndex is < 0
                || pvar.SourceTableIndex is { } sourceIndex && sourceIndex >= state.SourceTableEntryCount)
                throw new InvalidDataException($"Moby PVar owner {pvar.EntityId} has an invalid source table index.");
            if (pvar.Data is null || pvar.Data.Length > MobyDexSchema.MaximumPVarBytes
                || pvar.BaselineData is null || pvar.BaselineData.Length != pvar.Data.Length
                || pvar.MobyLinkOffsets is null || pvar.RelativePointerOffsets is null || pvar.References is null)
                throw new InvalidDataException($"Moby PVar owner {pvar.EntityId} has invalid data.");
            ValidateOffsets(pvar.MobyLinkOffsets, pvar.Data.Length, pvar.EntityId, "moby link");
            ValidateOffsets(pvar.RelativePointerOffsets, pvar.Data.Length, pvar.EntityId, "relative pointer");
            if (pvar.References.Any(value => value is null)
                || pvar.References.Select(value => value.Reference.FieldKey).Distinct(StringComparer.Ordinal).Count()
                != pvar.References.Count)
                throw new InvalidDataException($"Moby PVar owner {pvar.EntityId} has duplicate reference keys.");
            foreach (var reference in pvar.References)
            {
                if (reference.Offset < 0 || reference.Length != 4
                    || reference.Offset > pvar.Data.Length - reference.Length
                    || reference.Reference is not
                    {
                        Domain: ProjectReferenceDomain.Entity,
                        EntityKind: not null,
                        AssetKind: null,
                        AssetId: null,
                    }
                    || string.IsNullOrWhiteSpace(reference.Reference.FieldKey)
                    || reference.Reference.EntityKind == ProjectEntityKind.Moby
                        && !pvar.MobyLinkOffsets.Contains(reference.Offset))
                    throw new InvalidDataException($"Moby PVar owner {pvar.EntityId} has an invalid reference.");
                if (reference.Reference.EntityId is { } target
                    && (!entities.TryGetValue(target, out var targetEntity)
                        || reference.Reference.EntityKind != ProjectReferences.KindOf(targetEntity)))
                    throw new InvalidDataException($"Moby PVar owner {pvar.EntityId} has an invalid target {target}.");
            }
        }
    }

    private static void ValidateOffsets(
        IReadOnlyList<int> offsets,
        int length,
        EntityId entityId,
        string description)
    {
        if (offsets.Distinct().Count() != offsets.Count
            || offsets.Any(offset => offset < 0 || offset % 4 != 0 || offset > length - 4))
            throw new InvalidDataException($"Moby PVar owner {entityId} has invalid {description} offsets.");
    }
}

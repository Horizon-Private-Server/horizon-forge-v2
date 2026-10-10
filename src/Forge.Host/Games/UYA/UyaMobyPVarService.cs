using System.Buffers.Binary;
using Forge.Host.Domain;
using RatchetPs2.Core.Gameplay;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.Games.UYA;

internal static class UyaMobyPVarService
{
    public static Task ExecuteInitializeAsync(
        ForgeProjectWorkspace workspace,
        EditorCommand command,
        MobyDexCatalog catalog,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreateDefault(workspace, catalog, command.EntityIds.Single());
        return Task.CompletedTask;
    }

    public static ProjectMobyPVar CreateDefault(
        ForgeProjectWorkspace workspace,
        MobyDexCatalog catalog,
        EntityId entityId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(catalog);
        var entity = workspace.Content.Entities.SingleOrDefault(value => value.EntityId == entityId)
            ?? throw new KeyNotFoundException($"Entity {entityId} is not present in the project.");
        if (entity.Layer != "mobys" || entity.Source is null)
            throw new InvalidOperationException($"Entity {entityId} is not a UYA moby.");
        var definition = workspace.ResolveMobyDexEntry(catalog, "UYA", entity.Source.ClassId)?.Entry.PVar
            ?? throw new InvalidOperationException($"Moby class 0x{entity.Source.ClassId:X4} has no PVar schema.");
        if (definition.DefaultHex is null)
            throw new InvalidOperationException($"Moby class 0x{entity.Source.ClassId:X4} has no verified PVar default.");
        var data = Convert.FromHexString(definition.DefaultHex);
        var references = ResolveReferences(data, definition, SourceEntityIndexes(workspace.Content.Entities));
        var pvar = new ProjectMobyPVar(
            entityId,
            null,
            data,
            data.ToArray(),
            references.Where(value => value.Reference.EntityKind == ProjectEntityKind.Moby)
                .Select(value => value.Offset).Order().ToArray(),
            definition.Relocations.Order().ToArray(),
            references);
        workspace.AddMobyPVar(pvar);
        return pvar;
    }

    public static ProjectMobyPVarState? Import(
        IReadOnlyList<ProjectEntity> entities,
        GameplayPvarTables? tables,
        MobyDexCatalog catalog)
    {
        if (tables is null) return null;
        var mobys = entities.Where(value => value.Layer == "mobys")
            .OrderBy(value => value.Provenance?.SourceIndex ?? int.MaxValue)
            .ThenBy(value => value.EntityId.ToString(), StringComparer.Ordinal)
            .ToArray();
        var entityIndexes = SourceEntityIndexes(entities);
        var mobyLinks = tables.MobyLinks.GroupBy(value => value.PvarIndex)
            .ToDictionary(group => group.Key, group => group.Select(value => value.Offset).ToArray());
        var relativePointers = tables.RelativePointers.GroupBy(value => value.PvarIndex)
            .ToDictionary(group => group.Key, group => group.Select(value => value.Offset).Order().ToArray());
        var usedSourceIndexes = new HashSet<int>();
        var entries = new List<ProjectMobyPVar>();
        foreach (var entity in mobys)
        {
            if (entity.Source?.RawRecord.Length != UyaMobyInstancesReader.RecordSize) continue;
            var instance = UyaMobyInstancesReader.ReadInstance(entity.Source.RawRecord);
            if (instance.PvarIndex < 0 || instance.PvarIndex >= tables.Entries.Count) continue;
            var definition = catalog.Resolve(null, "UYA", entity.Source.ClassId)?.Entry.PVar;
            var source = tables.Entries[instance.PvarIndex];
            var references = definition?.Length == source.Length
                ? ResolveReferences(source.Data, definition, entityIndexes)
                : [];
            var declaredMobyLinks = references
                .Where(value => value.Reference.EntityKind == ProjectEntityKind.Moby)
                .Select(value => value.Offset);
            entries.Add(new(
                entity.EntityId,
                usedSourceIndexes.Add(instance.PvarIndex) ? instance.PvarIndex : null,
                source.Data.ToArray(),
                source.Data.ToArray(),
                (mobyLinks.GetValueOrDefault(instance.PvarIndex) ?? [])
                    .Concat(declaredMobyLinks).Distinct().Order().ToArray(),
                relativePointers.GetValueOrDefault(instance.PvarIndex) ?? [],
                references));
        }
        return new(ProjectMobyPVarSchema.CurrentVersion, tables.Entries.Count, entries);
    }

    public static async Task<EditorDiagnostic?> HydrateFromSourceAsync(
        ForgeProjectWorkspace workspace,
        MobyDexCatalog catalog,
        CancellationToken cancellationToken)
    {
        if (workspace.Manifest.Target.Game != "UYA") return null;
        try
        {
            var tables = await UyaGameplayLayerStore.ReadSourcePvarTablesAsync(workspace, cancellationToken);
            var imported = Import(workspace.Content.Entities, tables, catalog);
            if (imported is null) return null;
            var added = workspace.MergeMissingMobyPVars(imported);
            return added == 0 ? null : new(
                "moby.pvar-source-hydrated",
                EditorDiagnosticSeverity.Info,
                $"Recovered raw PVar payloads for {added} moby instance(s) from the retained level source. Save the project to persist them.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(
                "moby.pvar-source-hydration-failed",
                EditorDiagnosticSeverity.Warning,
                $"Raw PVar payloads could not be refreshed from the retained level source ({exception.Message}).");
        }
    }

    public static IReadOnlyDictionary<EntityId, int> NativeIndexes(ForgeProjectContent content)
    {
        if (content.MobyPVars is not { } state) return new Dictionary<EntityId, int>();
        var next = state.SourceTableEntryCount;
        var result = new Dictionary<EntityId, int>();
        var pvars = state.Entries.ToDictionary(value => value.EntityId);
        foreach (var entity in OrderedMobys(content))
        {
            pvars.TryGetValue(entity.EntityId, out var pvar);
            if (pvar is not null) result.Add(entity.EntityId, pvar.SourceTableIndex ?? next++);
        }
        return result;
    }

    public static GameplayPvarTables Rebuild(
        ForgeProjectContent content,
        GameplayPvarTables source,
        ICollection<string> blockers)
    {
        if (content.MobyPVars is not { } state) return source;
        if (state.SourceTableEntryCount != source.Entries.Count)
        {
            blockers.Add("Project Moby PVar source table count no longer matches the verified gameplay source.");
            return source;
        }

        var sourceMobyLinks = source.MobyLinks.GroupBy(value => value.PvarIndex)
            .ToDictionary(group => group.Key, group => group.Select(value => value.Offset).ToArray());
        var sourcePointers = source.RelativePointers.GroupBy(value => value.PvarIndex)
            .ToDictionary(group => group.Key, group => group.Select(value => value.Offset).ToArray());
        var entries = source.Entries.Select(value => new GameplayPvarBuildEntry(
            value.Data.ToArray(),
            sourceMobyLinks.GetValueOrDefault(value.Index) ?? [],
            sourcePointers.GetValueOrDefault(value.Index) ?? [])).ToList();
        var nativeIndexes = NativeIndexes(content);
        var pvars = state.Entries.ToDictionary(value => value.EntityId);
        var orderedMobys = OrderedMobys(content);
        var targetMobyIndexes = orderedMobys.Select((entity, index) => (entity.EntityId, index))
            .ToDictionary(value => value.EntityId, value => value.index);
        var structuralChange = orderedMobys
            .Where(value => value.Provenance is not null)
            .Select((value, index) => value.Provenance!.SourceIndex == index)
            .Any(value => !value)
            || content.Entities.Any(value => value.Layer == "mobys" && value.State?.Disabled == true)
            || content.Entities.Any(value => value.Layer == "mobys" && value.Provenance is null);
        var understoodIndexes = state.Entries.Select(value => value.SourceTableIndex).OfType<int>().ToHashSet();
        foreach (var link in source.MobyLinks.Where(value => !understoodIndexes.Contains(value.PvarIndex)))
        {
            var target = BinaryPrimitives.ReadInt32LittleEndian(
                source.Entries[link.PvarIndex].Data.AsSpan(link.Offset, 4));
            if (structuralChange || target >= orderedMobys.Length)
                blockers.Add("Moby order changed while an opaque PVar moby link still uses native indexes.");
        }

        foreach (var entity in orderedMobys)
        {
            pvars.TryGetValue(entity.EntityId, out var pvar);
            if (pvar is null) continue;
            var data = pvar.Data.ToArray();
            foreach (var reference in pvar.References)
            {
                if (reference.Reference.EntityKind != ProjectEntityKind.Moby) continue;
                var value = reference.Reference.EntityId is { } target
                    ? targetMobyIndexes.TryGetValue(target, out var targetIndex)
                        ? targetIndex
                        : AddMissingTarget(blockers, entity, reference)
                    : checked((int)reference.NullValue);
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(reference.Offset, 4), value);
            }
            var knownOffsets = pvar.References.Where(value => value.Reference.EntityKind == ProjectEntityKind.Moby)
                .Select(value => value.Offset).ToHashSet();
            foreach (var offset in pvar.MobyLinkOffsets.Where(offset => !knownOffsets.Contains(offset)))
            {
                var target = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
                if (structuralChange || target >= orderedMobys.Length)
                    blockers.Add($"{entity.Name} ({entity.EntityId}) has an opaque PVar moby link that cannot be remapped.");
            }
            var links = pvar.MobyLinkOffsets.Where(offset =>
                BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)) >= 0).ToArray();
            var rebuilt = new GameplayPvarBuildEntry(data, links, pvar.RelativePointerOffsets.ToArray());
            var nativeIndex = nativeIndexes[entity.EntityId];
            if (nativeIndex < entries.Count) entries[nativeIndex] = rebuilt;
            else if (nativeIndex == entries.Count) entries.Add(rebuilt);
            else blockers.Add($"{entity.Name} ({entity.EntityId}) has a non-contiguous PVar table index.");
        }
        return blockers.Count == 0 ? GameplayPvarTableWriter.Write(entries) : source;
    }

    private static int AddMissingTarget(
        ICollection<string> blockers,
        ProjectEntity entity,
        ProjectMobyPVarReference reference)
    {
        blockers.Add($"{entity.Name} ({entity.EntityId}) PVar field {reference.Reference.FieldKey} "
            + $"references unavailable moby {reference.Reference.EntityId}.");
        return checked((int)reference.NullValue);
    }

    private static ProjectMobyPVarReference[] ResolveReferences(
        byte[] data,
        MobyDexPVar definition,
        IReadOnlyDictionary<(ProjectEntityKind Kind, int SourceIndex), EntityId> indexes)
    {
        return ReferenceSlots(definition.Fields, 0, string.Empty).Where(value => value.Definition.Domain == "entity")
            .Select(value =>
            {
                var targetKind = TargetKind(value.Definition.TargetKind!);
                var sourceValue = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(value.Offset, 4));
                EntityId? target = indexes.TryGetValue((targetKind, sourceValue), out var resolved)
                    ? resolved
                    : null;
                var nullValue = value.Definition.NullValue!.Value;
                return new ProjectMobyPVarReference(
                    value.Offset,
                    4,
                    nullValue,
                    sourceValue,
                    new(ProjectReferenceDomain.Entity, value.Path, true, targetKind,
                        sourceValue == nullValue ? null : target));
            }).ToArray();
    }

    private static IReadOnlyDictionary<(ProjectEntityKind Kind, int SourceIndex), EntityId> SourceEntityIndexes(
        IReadOnlyList<ProjectEntity> entities) => entities
        .Select(entity => (Entity: entity, SourceIndex: entity.Provenance?.SourceIndex ?? entity.Source?.SourceIndex))
        .Where(value => value.SourceIndex is not null)
        .GroupBy(value => (ProjectReferences.KindOf(value.Entity), value.SourceIndex!.Value))
        .Where(group => group.Take(2).Count() == 1)
        .ToDictionary(group => group.Key, group => group.First().Entity.EntityId);

    private static IEnumerable<ReferenceSlot> ReferenceSlots(
        IReadOnlyList<MobyDexField> fields,
        int baseOffset,
        string prefix)
    {
        foreach (var field in fields)
        {
            var path = string.IsNullOrEmpty(prefix) ? field.Key : $"{prefix}.{field.Key}";
            foreach (var slot in ReferenceSlots(
                field.Definition!, baseOffset + field.Offset, path)) yield return slot;
        }
    }

    private static IEnumerable<ReferenceSlot> ReferenceSlots(
        MobyDexTypeDefinition definition,
        int offset,
        string path)
    {
        if (definition is { Kind: "reference", Storage: "int32" or "uint32" })
        {
            yield return new(path, offset, definition);
            yield break;
        }
        if (definition.Kind is "struct" or "union" && definition.Fields is { } fields)
        {
            foreach (var slot in ReferenceSlots(fields, offset, path)) yield return slot;
            yield break;
        }
        if (definition is { Kind: "array", Count: { } count, Stride: { } stride, Element: { } element })
            for (var index = 0; index < count; index++)
                foreach (var slot in ReferenceSlots(element, offset + index * stride, $"{path}[{index}]"))
                    yield return slot;
    }

    internal static ProjectEntityKind TargetKind(string value) => value switch
    {
        "entity" => ProjectEntityKind.Entity,
        "moby" => ProjectEntityKind.Moby,
        "tie" => ProjectEntityKind.Tie,
        "shrub" => ProjectEntityKind.Shrub,
        "tfrag" => ProjectEntityKind.Tfrag,
        "cuboid" => ProjectEntityKind.Cuboid,
        "sphere" => ProjectEntityKind.Sphere,
        "cylinder" => ProjectEntityKind.Cylinder,
        "pill" => ProjectEntityKind.Pill,
        "spline" => ProjectEntityKind.Spline,
        "grindPath" => ProjectEntityKind.GrindPath,
        "area" => ProjectEntityKind.Area,
        "collision" => ProjectEntityKind.Collision,
        "skyShell" => ProjectEntityKind.SkyShell,
        "directionalLight" => ProjectEntityKind.DirectionalLight,
        "pointLight" => ProjectEntityKind.PointLight,
        "environmentSample" => ProjectEntityKind.EnvironmentSample,
        "environmentTransition" => ProjectEntityKind.EnvironmentTransition,
        "camera" => ProjectEntityKind.Camera,
        "ambientSound" => ProjectEntityKind.AmbientSound,
        _ => throw new InvalidDataException($"Unsupported MobyDex entity target kind {value}."),
    };

    private static ProjectEntity[] OrderedMobys(ForgeProjectContent content) => content.Entities
        .Where(entity => entity.Layer == "mobys" && entity.State?.Disabled != true)
        .OrderBy(entity => entity.Provenance?.SourceIndex ?? int.MaxValue)
        .ThenBy(entity => entity.EntityId.ToString(), StringComparer.Ordinal)
        .ToArray();

    private sealed record ReferenceSlot(string Path, int Offset, MobyDexTypeDefinition Definition);
}

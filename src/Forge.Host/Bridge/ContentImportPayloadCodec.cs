using Forge.Host.Domain;
using static Forge.Host.Bridge.PayloadFormat;

namespace Forge.Host.Bridge;

public static partial class BridgePayloadCodec
{
    private const int MaxContentImportFamilies = 32;

    public static byte[] EncodeContentImportCapabilityRequest(ContentImportCapabilityRequestPayload value)
    {
        if (value.Inventory.Count > MaxContentImportFamilies) Malformed("Import family inventory exceeds limit");
        var writer = new PayloadWriter();
        writer.WriteString(value.TargetGame);
        writer.WriteString(value.TargetRegion);
        writer.WriteString(value.TargetRevision);
        writer.WriteUInt32((uint)value.Inventory.Count);
        foreach (var item in value.Inventory)
        {
            writer.WriteString(item.Family);
            writer.WriteUInt32(item.State);
            writer.WriteBoolean(item.Diagnostic is not null);
            if (item.Diagnostic is not null) writer.WriteString(item.Diagnostic);
        }
        return writer.ToArray();
    }

    public static ContentImportCapabilityRequestPayload DecodeContentImportCapabilityRequest(
        ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var game = reader.ReadString();
        var region = reader.ReadString();
        var revision = reader.ReadString();
        var count = reader.ReadUInt32();
        if (count > MaxContentImportFamilies) Malformed("Import family inventory exceeds limit");
        var inventory = new ContentImportFamilyInventoryPayload[count];
        for (var index = 0; index < inventory.Length; index++)
            inventory[index] = new(
                reader.ReadString(), reader.ReadUInt32(), reader.ReadBoolean() ? reader.ReadString() : null);
        reader.Complete();
        return new(game, region, revision, inventory);
    }

    public static byte[] EncodeContentImportCapabilityMatrix(ContentImportCapabilityMatrixPayload value)
    {
        if (value.Families.Count > MaxContentImportFamilies) Malformed("Import capability matrix exceeds limit");
        var writer = new PayloadWriter();
        writer.WriteUInt32(value.SchemaVersion);
        writer.WriteString(value.Game);
        writer.WriteString(value.Region);
        writer.WriteString(value.Revision);
        writer.WriteUInt32((uint)value.Families.Count);
        foreach (var family in value.Families)
        {
            writer.WriteString(family.Family);
            writer.WriteString(family.Label);
            writer.WriteUInt32(family.Mode);
            writer.WriteUInt32(family.SourceState);
            writer.WriteBoolean(family.ImplementationReady);
            writer.WriteBoolean(family.Selectable);
            writer.WriteBoolean(family.DefaultSelectedWhenReady);
            writer.WriteBoolean(family.DefaultSelected);
            writer.WriteBoolean(family.DisabledReason is not null);
            if (family.DisabledReason is not null) writer.WriteString(family.DisabledReason);
            writer.WriteString(family.Ownership);
            writer.WriteStrings(family.Dependencies);
            writer.WriteString(family.ConflictNamespace);
            writer.WriteString(family.NativeSemantics);
            writer.WriteString(family.ProtectedSurvivorPolicy);
            writer.WriteString(family.NoEditIdentity);
            writer.WriteStrings(family.Limits);
            writer.WriteStrings(family.Evidence);
        }
        return writer.ToArray();
    }

    public static ContentImportCapabilityMatrixPayload DecodeContentImportCapabilityMatrix(
        ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var schemaVersion = reader.ReadUInt32();
        var game = reader.ReadString();
        var region = reader.ReadString();
        var revision = reader.ReadString();
        var count = reader.ReadUInt32();
        if (count > MaxContentImportFamilies) Malformed("Import capability matrix exceeds limit");
        var families = new ContentImportFamilyCapabilityPayload[count];
        for (var index = 0; index < families.Length; index++)
        {
            families[index] = new(
                reader.ReadString(),
                reader.ReadString(),
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                reader.ReadBoolean(),
                reader.ReadBoolean(),
                reader.ReadBoolean(),
                reader.ReadBoolean(),
                reader.ReadBoolean() ? reader.ReadString() : null,
                reader.ReadString(),
                reader.ReadStrings(),
                reader.ReadString(),
                reader.ReadString(),
                reader.ReadString(),
                reader.ReadString(),
                reader.ReadStrings(),
                reader.ReadStrings());
        }
        reader.Complete();
        return new(schemaVersion, game, region, revision, families);
    }

    public static byte[] EncodeContentImportPlanRequest(ContentImportPlanRequestPayload value)
    {
        if (value.Selections.Count > MaxContentImportFamilies) Malformed("Import selection list exceeds limit");
        var writer = new PayloadWriter();
        writer.WriteString(value.ProjectPath);
        writer.WriteString(value.CatalogRootPath);
        writer.WriteString(value.SourceIsoPath);
        writer.WriteString(value.SourceFingerprint);
        writer.WriteUInt32(value.SourceLevel);
        writer.WriteUInt32((uint)value.Selections.Count);
        foreach (var selection in value.Selections)
        {
            writer.WriteString(selection.Family);
            writer.WriteUInt32(selection.Mode);
        }
        return writer.ToArray();
    }

    public static ContentImportPlanRequestPayload DecodeContentImportPlanRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var projectPath = reader.ReadString();
        var catalogRootPath = reader.ReadString();
        var sourceIsoPath = reader.ReadString();
        var sourceFingerprint = reader.ReadString();
        var sourceLevel = reader.ReadUInt32();
        var count = reader.ReadUInt32();
        if (count > MaxContentImportFamilies) Malformed("Import selection list exceeds limit");
        var selections = new ContentImportSelectionPayload[count];
        for (var index = 0; index < selections.Length; index++)
            selections[index] = new(reader.ReadString(), reader.ReadUInt32());
        reader.Complete();
        return new(projectPath, catalogRootPath, sourceIsoPath, sourceFingerprint, sourceLevel, selections);
    }

    public static byte[] EncodeContentImportApplyRequest(ContentImportApplyRequestPayload value)
    {
        if (value.Selections.Count > MaxContentImportFamilies) Malformed("Import selection list exceeds limit");
        if (value.ReferenceChoices.Count > ContentImportPlanSchema.MaximumPlanItems)
            Malformed("Import reference choice list exceeds limit");
        var writer = new PayloadWriter();
        writer.WriteString(value.ProjectPath);
        writer.WriteString(value.CatalogRootPath);
        writer.WriteString(value.SourceIsoPath);
        writer.WriteString(value.SourceFingerprint);
        writer.WriteUInt32(value.SourceLevel);
        writer.WriteString(value.BatchId);
        writer.WriteUInt32((uint)value.Selections.Count);
        foreach (var selection in value.Selections)
        {
            writer.WriteString(selection.Family);
            writer.WriteUInt32(selection.Mode);
        }
        writer.WriteUInt32((uint)value.ReferenceChoices.Count);
        foreach (var choice in value.ReferenceChoices)
        {
            writer.WriteInt32(choice.OwnerSourceIndex);
            writer.WriteString(choice.FieldKey);
            writer.WriteInt32(choice.SourceValue);
            writer.WriteUInt32(choice.Action);
            writer.WriteBoolean(choice.TargetEntityId is not null);
            if (choice.TargetEntityId is not null) writer.WriteString(choice.TargetEntityId);
        }
        return writer.ToArray();
    }

    public static ContentImportApplyRequestPayload DecodeContentImportApplyRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var projectPath = reader.ReadString();
        var catalogRootPath = reader.ReadString();
        var sourceIsoPath = reader.ReadString();
        var sourceFingerprint = reader.ReadString();
        var sourceLevel = reader.ReadUInt32();
        var batchId = reader.ReadString();
        var count = reader.ReadUInt32();
        if (count > MaxContentImportFamilies) Malformed("Import selection list exceeds limit");
        var selections = new ContentImportSelectionPayload[count];
        for (var index = 0; index < selections.Length; index++)
            selections[index] = new(reader.ReadString(), reader.ReadUInt32());
        count = reader.ReadUInt32();
        if (count > ContentImportPlanSchema.MaximumPlanItems)
            Malformed("Import reference choice list exceeds limit");
        var referenceChoices = new ContentImportReferenceChoicePayload[count];
        for (var index = 0; index < referenceChoices.Length; index++)
            referenceChoices[index] = new(
                reader.ReadInt32(),
                reader.ReadString(),
                reader.ReadInt32(),
                reader.ReadUInt32(),
                reader.ReadBoolean() ? reader.ReadString() : null);
        reader.Complete();
        return new(projectPath, catalogRootPath, sourceIsoPath, sourceFingerprint,
            sourceLevel, batchId, selections, referenceChoices);
    }

    public static byte[] EncodeContentImportPlan(ContentImportPlanPayload value)
    {
        if (value.Families.Count > MaxContentImportFamilies) Malformed("Import plan family list exceeds limit");
        var writer = new PayloadWriter();
        writer.WriteUInt32(value.SchemaVersion);
        writer.WriteUInt32(value.CapabilitySchemaVersion);
        writer.WriteString(value.BatchId);
        writer.WriteString(value.Game);
        writer.WriteString(value.Region);
        writer.WriteString(value.Revision);
        writer.WriteString(value.SourceFingerprint);
        writer.WriteString(value.SourceLevelHash);
        writer.WriteUInt32(value.SourceLevel);
        writer.WriteString(value.TargetProjectId);
        writer.WriteString(value.TargetFingerprint);
        writer.WriteUInt32(value.TargetLevel);
        writer.WriteUInt64(value.EstimatedBytes);
        writer.WriteUInt32((uint)value.Families.Count);
        foreach (var family in value.Families)
        {
            writer.WriteString(family.Family);
            writer.WriteString(family.Label);
            writer.WriteUInt32(family.SupportedMode);
            writer.WriteUInt32(family.Mode);
            writer.WriteUInt32(family.SourceState);
            writer.WriteBoolean(family.Selected);
            writer.WriteBoolean(family.DefaultSelected);
            writer.WriteBoolean(family.Selectable);
            WritePlanItems(writer, family.Add);
            WritePlanItems(writer, family.Reuse);
            WritePlanItems(writer, family.Remap);
            WritePlanItems(writer, family.Remove);
            WritePlanItems(writer, family.Protect);
            writer.WriteStrings(family.DependencyReasons);
            WritePlanItems(writer, family.Conflicts);
            writer.WriteUInt64(family.EstimatedBytes);
            writer.WriteStrings(family.Warnings);
            writer.WriteStrings(family.Blockers);
        }
        writer.WriteStrings(value.Warnings);
        writer.WriteStrings(value.Blockers);
        if (value.ReferenceRequirements.Count > ContentImportPlanSchema.MaximumPlanItems)
            Malformed("Import reference requirement list exceeds limit");
        writer.WriteUInt32((uint)value.ReferenceRequirements.Count);
        foreach (var requirement in value.ReferenceRequirements)
        {
            writer.WriteString(requirement.Family);
            writer.WriteInt32(requirement.OwnerSourceIndex);
            writer.WriteString(requirement.FieldKey);
            writer.WriteInt32(requirement.SourceValue);
            writer.WriteUInt32(requirement.TargetKind);
            writer.WriteBoolean(requirement.Nullable);
            WritePlanItems(writer, requirement.CandidateEntityIds);
        }
        return writer.ToArray();
    }

    public static ContentImportPlanPayload DecodeContentImportPlan(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var schemaVersion = reader.ReadUInt32();
        var capabilitySchemaVersion = reader.ReadUInt32();
        var batchId = reader.ReadString();
        var game = reader.ReadString();
        var region = reader.ReadString();
        var revision = reader.ReadString();
        var sourceFingerprint = reader.ReadString();
        var sourceLevelHash = reader.ReadString();
        var sourceLevel = reader.ReadUInt32();
        var targetProjectId = reader.ReadString();
        var targetFingerprint = reader.ReadString();
        var targetLevel = reader.ReadUInt32();
        var estimatedBytes = reader.ReadUInt64();
        var count = reader.ReadUInt32();
        if (count > MaxContentImportFamilies) Malformed("Import plan family list exceeds limit");
        var families = new ContentImportFamilyPlanPayload[count];
        for (var index = 0; index < families.Length; index++)
            families[index] = new(
                reader.ReadString(), reader.ReadString(), reader.ReadUInt32(),
                reader.ReadUInt32(), reader.ReadUInt32(),
                reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadBoolean(),
                ReadPlanItems(ref reader), ReadPlanItems(ref reader), ReadPlanItems(ref reader),
                ReadPlanItems(ref reader), ReadPlanItems(ref reader),
                reader.ReadStrings(), ReadPlanItems(ref reader), reader.ReadUInt64(),
                reader.ReadStrings(), reader.ReadStrings());
        var warnings = reader.ReadStrings();
        var blockers = reader.ReadStrings();
        count = reader.ReadUInt32();
        if (count > ContentImportPlanSchema.MaximumPlanItems)
            Malformed("Import reference requirement list exceeds limit");
        var referenceRequirements = new ContentImportReferenceRequirementPayload[count];
        for (var index = 0; index < referenceRequirements.Length; index++)
            referenceRequirements[index] = new(
                reader.ReadString(),
                reader.ReadInt32(),
                reader.ReadString(),
                reader.ReadInt32(),
                reader.ReadUInt32(),
                reader.ReadBoolean(),
                ReadPlanItems(ref reader));
        reader.Complete();
        return new(schemaVersion, capabilitySchemaVersion, batchId, game, region, revision,
            sourceFingerprint, sourceLevelHash, sourceLevel, targetProjectId, targetFingerprint,
            targetLevel, estimatedBytes, families, warnings, blockers, referenceRequirements);
    }

    private static void WritePlanItems(PayloadWriter writer, IReadOnlyList<string> values)
    {
        if (values.Count > ContentImportPlanSchema.MaximumPlanItems) Malformed("Import plan item list exceeds limit");
        writer.WriteUInt32((uint)values.Count);
        foreach (var value in values) writer.WriteString(value);
    }

    private static string[] ReadPlanItems(ref PayloadReader reader)
    {
        var count = reader.ReadUInt32();
        if (count > ContentImportPlanSchema.MaximumPlanItems) Malformed("Import plan item list exceeds limit");
        var values = new string[count];
        for (var index = 0; index < values.Length; index++) values[index] = reader.ReadString();
        return values;
    }

    public static byte[] EncodeContentImportRemovalPlanRequest(ContentImportRemovalPlanRequestPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.ProjectPath);
        writer.WriteString(value.BatchId);
        return writer.ToArray();
    }

    public static ContentImportRemovalPlanRequestPayload DecodeContentImportRemovalPlanRequest(
        ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var result = new ContentImportRemovalPlanRequestPayload(reader.ReadString(), reader.ReadString());
        reader.Complete();
        return result;
    }

    public static byte[] EncodeContentImportRemovalApplyRequest(ContentImportRemovalApplyRequestPayload value)
    {
        var writer = new PayloadWriter();
        writer.WriteString(value.ProjectPath);
        writer.WriteString(value.BatchId);
        writer.WriteString(value.TargetFingerprint);
        return writer.ToArray();
    }

    public static ContentImportRemovalApplyRequestPayload DecodeContentImportRemovalApplyRequest(
        ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var result = new ContentImportRemovalApplyRequestPayload(
            reader.ReadString(), reader.ReadString(), reader.ReadString());
        reader.Complete();
        return result;
    }

    public static byte[] EncodeContentImportRemovalPlan(ContentImportRemovalPlanPayload value)
    {
        if (value.Delete.Count > ContentImportPlanSchema.MaximumPlanItems
            || value.Survivors.Count > ContentImportPlanSchema.MaximumPlanItems
            || value.Shared.Count > ContentImportPlanSchema.MaximumPlanItems
            || value.Protected.Count > ContentImportPlanSchema.MaximumPlanItems)
            Malformed("Import removal plan exceeds item limit");
        var writer = new PayloadWriter();
        writer.WriteUInt32(value.SchemaVersion);
        writer.WriteString(value.BatchId);
        writer.WriteString(value.TargetFingerprint);
        WritePlanItems(writer, value.Delete);
        writer.WriteUInt32((uint)value.Survivors.Count);
        foreach (var survivor in value.Survivors)
        {
            writer.WriteString(survivor.StableId);
            writer.WriteString(survivor.Reason);
        }
        WritePlanItems(writer, value.Shared);
        WritePlanItems(writer, value.Protected);
        writer.WriteStrings(value.Warnings);
        return writer.ToArray();
    }

    public static ContentImportRemovalPlanPayload DecodeContentImportRemovalPlan(ReadOnlySpan<byte> payload)
    {
        var reader = new PayloadReader(payload);
        var schemaVersion = reader.ReadUInt32();
        var batchId = reader.ReadString();
        var targetFingerprint = reader.ReadString();
        var delete = ReadPlanItems(ref reader);
        var count = reader.ReadUInt32();
        if (count > ContentImportPlanSchema.MaximumPlanItems)
            Malformed("Import removal survivor list exceeds item limit");
        var survivors = new ContentImportRemovalSurvivorPayload[count];
        for (var index = 0; index < survivors.Length; index++)
            survivors[index] = new(reader.ReadString(), reader.ReadString());
        var shared = ReadPlanItems(ref reader);
        var protectedItems = ReadPlanItems(ref reader);
        var warnings = reader.ReadStrings();
        reader.Complete();
        return new(schemaVersion, batchId, targetFingerprint, delete, survivors, shared, protectedItems, warnings);
    }
}

using Forge.Host.Domain;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.Games.UYA;

internal static class UyaMobyPropertyService
{
    internal const string Mission = "instance.mission";
    internal const string Uid = "instance.uid";
    internal const string Bolts = "instance.bolts";
    internal const string ClassId = "instance.classId";
    internal const string DrawDistance = "instance.drawDistance";
    internal const string UpdateDistance = "instance.updateDistance";
    internal const string Group = "instance.group";
    internal const string IsRooted = "instance.isRooted";
    internal const string RootedDistance = "instance.rootedDistance";
    internal const string Pvar = "instance.pvar";
    internal const string Occlusion = "instance.occlusion";
    internal const string ModeBits = "instance.modeBits";
    internal const string Color = "instance.color";
    internal const string Light = "instance.light";

    public static IReadOnlyList<EditorMobyPropertyDescriptor>? Describe(ProjectEntity entity)
    {
        if (entity.Asset is { Kind: not AssetKind.Moby }
            || entity.Provenance is not { Game: "UYA", Section: "gameplay/core/moby_instances" }
            || entity.Source?.RawRecord.Length != UyaMobyInstancesReader.RecordSize)
            return null;
        var value = UyaMobyInstancesReader.ReadInstance(entity.Source.RawRecord);
        return
        [
            Integer(Mission, "Mission", value.Mission, true,
                UyaMobyInstanceFieldLimits.MissionMinimum, UyaMobyInstanceFieldLimits.MissionMaximum,
                help: "Use -1 for every mission, or a mission index from 0 through 127."),
            Integer(Uid, "UID", value.Uid, false, reason: "UID allocation is owned by the level."),
            Integer(Bolts, "Bolts", value.Bolts, true, UyaMobyInstanceFieldLimits.DistanceMinimum, int.MaxValue),
            Integer(ClassId, "OClass", value.ClassId, false,
                reason: "OClass is owned by the selected asset."),
            Integer(DrawDistance, "Draw distance", value.DrawDistance, true,
                UyaMobyInstanceFieldLimits.DistanceMinimum, int.MaxValue,
                unit: "units", help: "Maximum distance at which the moby is rendered."),
            Integer(UpdateDistance, "Update distance", value.UpdateDistance, true,
                UyaMobyInstanceFieldLimits.DistanceMinimum, int.MaxValue,
                unit: "units", help: "Maximum distance at which the moby is updated."),
            Integer(Group, "Group", value.Group, false, reason: "Group references are not writable yet."),
            new(IsRooted, "Rooted", BooleanValue(value.IsRooted != 0), true),
            new(RootedDistance, "Rooted distance", FloatValue(value.RootedDistance), true,
                FloatMinimum: UyaMobyInstanceFieldLimits.RootedDistanceNone, FloatMaximum: float.MaxValue,
                Unit: "units", Help: "Use -1 for no limit, or a non-negative distance."),
            Integer(Pvar, "Pvar index", value.PvarIndex, false,
                reason: "Pvar allocation is owned by the level."),
            Integer(Occlusion, "Occlusion", value.Occlusion, false,
                reason: "Occlusion references are not writable yet."),
            Integer(ModeBits, "Mode bits", value.ModeBits, false,
                reason: "Mode flags are preserved until their semantics are verified."),
            new(Color, "Color", ColorValue(value.Color), true,
                IntegerMinimum: UyaMobyInstanceFieldLimits.ColorMinimum,
                IntegerMaximum: UyaMobyInstanceFieldLimits.ColorMaximum,
                Help: "Native red, green, and blue channels from 0 through 255."),
            Integer(Light, "Light", value.Light, false, reason: "Light references are not writable yet."),
        ];
    }

    public static Task ExecuteAsync(
        ForgeProjectWorkspace workspace,
        EditorCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var edit = command.MobyPropertyEdit
            ?? throw new ArgumentException("Moby property edit data is required.", nameof(command));
        var field = Field(edit.FieldKey);
        var value = Map(edit.Value);
        var updates = workspace.GetEntities(command.EntityIds).Select(entity =>
        {
            var source = entity.Source
                ?? throw new InvalidOperationException("Moby source metadata is unavailable.");
            if (Describe(entity) is null)
                throw new InvalidOperationException("A selected entity does not expose UYA moby properties.");
            if (edit.ExpectedClassId != source.ClassId)
                throw new InvalidOperationException("Moby OClass changed after the property editor was opened.");
            return (entity.EntityId, source.ClassId, UyaMobyInstancesWriter.WriteField(
                source.RawRecord, edit.ExpectedClassId, new(field, value)));
        }).ToArray();
        workspace.UpdateEntitySourceRecords(updates);
        return Task.CompletedTask;
    }

    private static UyaMobyInstanceField Field(string key) => key switch
    {
        Mission => UyaMobyInstanceField.Mission,
        Bolts => UyaMobyInstanceField.Bolts,
        DrawDistance => UyaMobyInstanceField.DrawDistance,
        UpdateDistance => UyaMobyInstanceField.UpdateDistance,
        IsRooted => UyaMobyInstanceField.IsRooted,
        RootedDistance => UyaMobyInstanceField.RootedDistance,
        Color => UyaMobyInstanceField.Color,
        _ => throw new ArgumentException($"Unknown or read-only moby property key '{key}'.", nameof(key)),
    };

    private static UyaMobyInstanceFieldValue Map(EditorMobyPropertyValue value) => value.Kind switch
    {
        EditorMobyPropertyValueKind.Integer => new(
            UyaMobyInstanceFieldValueKind.Integer, Integer: value.Integer),
        EditorMobyPropertyValueKind.Float => new(
            UyaMobyInstanceFieldValueKind.Float, Float: value.Float),
        EditorMobyPropertyValueKind.Boolean => new(
            UyaMobyInstanceFieldValueKind.Boolean, Boolean: value.Boolean),
        EditorMobyPropertyValueKind.Color => new(
            UyaMobyInstanceFieldValueKind.Color,
            Color: value.Color is { } color ? new(color.R, color.G, color.B) : null),
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static EditorMobyPropertyDescriptor Integer(
        string key,
        string label,
        int value,
        bool editable,
        int? minimum = null,
        int? maximum = null,
        string? unit = null,
        string? help = null,
        string? reason = null) => new(
            key, label, new(EditorMobyPropertyValueKind.Integer, Integer: value), editable,
            minimum, maximum, Unit: unit, Help: help, ReadOnlyReason: reason);

    private static EditorMobyPropertyValue FloatValue(float value) => new(
        EditorMobyPropertyValueKind.Float, Float: value);

    private static EditorMobyPropertyValue BooleanValue(bool value) => new(
        EditorMobyPropertyValueKind.Boolean, Boolean: value);

    private static EditorMobyPropertyValue ColorValue(UyaRgb96 value) => new(
        EditorMobyPropertyValueKind.Color, Color: new(value.Red, value.Green, value.Blue));
}

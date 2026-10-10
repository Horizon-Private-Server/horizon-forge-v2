namespace Forge.Host.Domain;

public sealed partial class EditorRuntime
{
    private void ValidateCommand(EditorCommand command, ForgeProjectWorkspace workspace)
    {
        ValidateCommandId(command.Id);
        if (!Enum.IsDefined(command.Kind)) throw new ArgumentOutOfRangeException(nameof(command), "Unknown editor command kind.");
        ArgumentNullException.ThrowIfNull(command.EntityIds);
        if (command.EntityIds.Count != command.EntityIds.Distinct().Count())
            throw new ArgumentException("Command Entity IDs must be unique.", nameof(command));
        var known = workspace.Content.Entities.Select(entity => entity.EntityId).ToHashSet();
        if (command.Kind != EditorCommandKind.RemoveGroupMembers
            && command.EntityIds.Any(id => !known.Contains(id)))
            throw new ArgumentException("Command references an entity that is not present in the active project.", nameof(command));
        if (command.Kind != EditorCommandKind.UpdateLevelSettings && command.LevelSettings is not null)
            throw new ArgumentException("Only level setting commands can contain level settings.", nameof(command));
        if (command.Kind != EditorCommandKind.UpdateSplinePoints && command.Points is not null)
            throw new ArgumentException("Only spline point commands can contain points.", nameof(command));
        if (command.Kind != EditorCommandKind.CreateEntityFromAsset && command.Placement is not null)
            throw new ArgumentException("Only asset placement commands can contain placement data.", nameof(command));
        if (command.Kind != EditorCommandKind.AddSkyShellFromAsset && command.SkyShellSource is not null)
            throw new ArgumentException("Only sky shell add commands can contain a source.", nameof(command));
        if (command.Kind != EditorCommandKind.UpdateSkyShell && command.SkyShellUpdate is not null)
            throw new ArgumentException("Only sky shell update commands can contain rotation values.", nameof(command));
        if (command.Kind != EditorCommandKind.ReorderSkyShell && command.DestinationOrder is not null)
            throw new ArgumentException("Only sky shell reorder commands can contain an order.", nameof(command));
        if (command.Kind != EditorCommandKind.SetInstancedCollisionEnabled && command.InstancedCollisionEnabled is not null)
            throw new ArgumentException("Only instanced collision toggle commands can contain an enabled value.", nameof(command));
        if (command.Kind != EditorCommandKind.SetInstancedCollisionRawType && command.InstancedCollisionRawType is not null)
            throw new ArgumentException("Only instanced collision ID commands can contain a raw type.", nameof(command));
        if (command.Kind != EditorCommandKind.SetInstancedCollisionFaceTypes
            && (command.InstancedCollisionProxyAssetId is not null || command.InstancedCollisionFaceTypes is not null))
            throw new ArgumentException("Only instanced collision face commands can contain face assignments.", nameof(command));
        if (command.Kind != EditorCommandKind.SetEntityReference && command.ReferenceUpdate is not null)
            throw new ArgumentException("Only reference commands can contain a reference update.", nameof(command));
        if (command.Kind != EditorCommandKind.UpdatePaletteOptimization && command.PaletteOptimization is not null)
            throw new ArgumentException(
                "Only palette optimization commands can contain a palette profile.", nameof(command));
        if (command.Kind is not (EditorCommandKind.ReplaceHudTexture
                or EditorCommandKind.RemoveHudTextureOverride
                or EditorCommandKind.AddHudIcon
                or EditorCommandKind.RemoveHudIcon)
            && command.HudEdit is not null)
            throw new ArgumentException("Only HUD commands can contain a HUD edit.", nameof(command));
        if (command.Kind is not (EditorCommandKind.ReplaceFxTexture
                or EditorCommandKind.RemoveFxTextureOverride
                or EditorCommandKind.AddFxTexture
                or EditorCommandKind.RemoveFxTexture)
            && command.FxEdit is not null)
            throw new ArgumentException("Only FX commands can contain an FX edit.", nameof(command));
        if (command.Kind != EditorCommandKind.SetMobyInstanceProperty && command.MobyPropertyEdit is not null)
            throw new ArgumentException("Only moby property commands can contain a moby property edit.", nameof(command));
        if (command.Kind is not (EditorCommandKind.ImportMobyDexEntry or EditorCommandKind.RemoveMobyDexEntry)
            && command.MobyDexEdit is not null)
            throw new ArgumentException("Only MobyDex commands can contain a MobyDex edit.", nameof(command));
        if (command.Kind != EditorCommandKind.SetMobyPVarField && command.MobyPVarEdit is not null)
            throw new ArgumentException("Only PVar field commands can contain a PVar edit.", nameof(command));
        if (command.Kind is not (EditorCommandKind.CreateGroup or EditorCommandKind.RenameGroup
                or EditorCommandKind.DeleteGroup or EditorCommandKind.ReorderGroup
                or EditorCommandKind.AddGroupMembers or EditorCommandKind.RemoveGroupMembers)
            && command.GroupEdit is not null)
            throw new ArgumentException("Only group commands can contain a group edit.", nameof(command));
        var locked = workspace.Content.Entities
            .Where(entity => entity.State?.Locked == true)
            .Select(entity => entity.EntityId)
            .ToHashSet();
        var readOnly = workspace.Content.Entities
            .Where(IsReadOnlySource)
            .Select(entity => entity.EntityId)
            .ToHashSet();
        var transformOnly = workspace.Content.Entities
            .Where(entity => IsDecodedSource(entity)
                && TransformCapabilities(entity) != EditorTransformCapabilities.None)
            .Select(entity => entity.EntityId)
            .ToHashSet();
        var readOnlyStateChange = command.Kind == EditorCommandKind.SetEntityState
            && command.State is { Locked: null };
        if (command.EntityIds.Any(readOnly.Contains)
            && command.Kind != EditorCommandKind.SetSelection
            && command.Kind is not (EditorCommandKind.AddGroupMembers or EditorCommandKind.RemoveGroupMembers)
            && !readOnlyStateChange)
            throw new ArgumentException("Decoded source data is read-only until its native writer is available.", nameof(command));
        if (command.EntityIds.Any(transformOnly.Contains)
            && workspace.GetEntities(command.EntityIds).Any(entity => entity.Collision is null)
            && (command.Kind is EditorCommandKind.DeleteEntities or EditorCommandKind.DuplicateEntities
                or EditorCommandKind.CopyEntities
                || command.Kind == EditorCommandKind.SetEntityState && command.State?.Disabled is not null))
            throw new ArgumentException("This decoded source type supports transform edits but not structural changes.", nameof(command));
        if (command.Kind is not (EditorCommandKind.AddGroupMembers or EditorCommandKind.RemoveGroupMembers)
            && command.EntityIds.Count > 0
            && workspace.GetEntities(command.EntityIds).Any(entity => entity.Collision is not null)
            && command.Kind is EditorCommandKind.DuplicateEntities or EditorCommandKind.CopyEntities
                or EditorCommandKind.SetEntityLayer)
            throw new ArgumentException("Collision pieces cannot be duplicated, copied, or moved to another layer.", nameof(command));
        var movesLockedCollisionParent = (command.Kind is EditorCommandKind.UpdateTransform
                or EditorCommandKind.UpdateTransforms)
            && workspace.GetEntities(command.EntityIds).Any(entity =>
                entity.Collision?.Attachment is { } attachment && locked.Contains(attachment.ParentEntityId));
        if ((command.EntityIds.Any(locked.Contains) || movesLockedCollisionParent)
            && command.Kind is EditorCommandKind.UpdateTransform or EditorCommandKind.UpdateTransforms
                or EditorCommandKind.RenameEntity
                or EditorCommandKind.SetEntityLayer
                or EditorCommandKind.DeleteEntities
                or EditorCommandKind.DuplicateEntities
                or EditorCommandKind.UpdateSplinePoints
                or EditorCommandKind.UpdateSkyShell
                or EditorCommandKind.ReorderSkyShell
                or EditorCommandKind.RemoveInstancedCollisionProxy
                or EditorCommandKind.SetInstancedCollisionEnabled
                or EditorCommandKind.SetInstancedCollisionRawType
                or EditorCommandKind.SetInstancedCollisionFaceTypes
                or EditorCommandKind.SetEntityReference
                or EditorCommandKind.SetMobyInstanceProperty
                or EditorCommandKind.InitializeMobyPVar
                or EditorCommandKind.SetMobyPVarField)
            throw new ArgumentException("Locked entities cannot be modified.", nameof(command));
        if (command.EntityIds.Any(locked.Contains)
            && command.Kind == EditorCommandKind.SetEntityState
            && command.State is not { Hidden: null, Disabled: null, Locked: false })
            throw new ArgumentException("Unlock an entity before changing its state.", nameof(command));
        switch (command.Kind)
        {
            case EditorCommandKind.SetSelection when command.Transform is not null || command.Text is not null || command.State is not null:
                throw new ArgumentException("Selection commands cannot contain mutation data.", nameof(command));
            case EditorCommandKind.RenameProject when command.EntityIds.Count != 0 || command.Transform is not null
                || string.IsNullOrWhiteSpace(command.Text) || command.State is not null:
                throw new ArgumentException("Rename commands require only a project name.", nameof(command));
            case EditorCommandKind.UpdateTransform when command.EntityIds.Count != 1 || command.Transform is null
                || command.Text is not null || command.State is not null || command.Transforms?.Count > 0:
                throw new ArgumentException("Transform commands require one entity and a transform.", nameof(command));
            case EditorCommandKind.UpdateTransforms when command.EntityIds.Count == 0 || command.Transform is not null
                || command.Text is not null || command.State is not null || command.Transforms is null
                || command.Transforms.Count != command.EntityIds.Count
                || !command.Transforms.Select(update => update.EntityId).SequenceEqual(command.EntityIds):
                throw new ArgumentException("Batch transform commands require one transform per entity in command order.", nameof(command));
            case EditorCommandKind.RenameEntity when command.EntityIds.Count != 1 || command.Transform is not null
                || string.IsNullOrWhiteSpace(command.Text) || command.State is not null:
                throw new ArgumentException("Entity rename commands require one entity and a name.", nameof(command));
            case EditorCommandKind.SetEntityLayer when command.EntityIds.Count == 0 || command.Transform is not null
                || string.IsNullOrWhiteSpace(command.Text) || command.State is not null:
                throw new ArgumentException("Layer commands require entities and a layer.", nameof(command));
            case EditorCommandKind.SetEntityState when command.EntityIds.Count == 0 || command.Transform is not null
                || command.Text is not null || command.State is null
                || (command.State.Hidden is null && command.State.Disabled is null && command.State.Locked is null):
                throw new ArgumentException("State commands require entities and at least one state change.", nameof(command));
            case EditorCommandKind.Undo or EditorCommandKind.Redo when command.EntityIds.Count != 0
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0:
                throw new ArgumentException("History commands cannot contain mutation data.", nameof(command));
            case EditorCommandKind.DeleteEntities or EditorCommandKind.DuplicateEntities or EditorCommandKind.CopyEntities
                when command.EntityIds.Count == 0 || command.Transform is not null || command.Text is not null
                || command.State is not null || command.Transforms?.Count > 0:
                throw new ArgumentException("Entity edit commands require only one or more entities.", nameof(command));
            case EditorCommandKind.PasteEntities when command.EntityIds.Count != 0 || command.Transform is not null
                || command.Text is not null || command.State is not null || command.Transforms?.Count > 0:
                throw new ArgumentException("Paste commands cannot contain mutation data.", nameof(command));
            case EditorCommandKind.UpdateLevelSettings when command.EntityIds.Count != 0 || command.Transform is not null
                || command.Text is not null || command.State is not null || command.Transforms?.Count > 0
                || command.LevelSettings is null:
                throw new ArgumentException("Level setting commands require only level settings.", nameof(command));
            case EditorCommandKind.UpdatePaletteOptimization when command.EntityIds.Count != 0
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0 || command.PaletteOptimization is null:
                throw new ArgumentException(
                    "Palette optimization commands require only a palette profile.", nameof(command));
            case EditorCommandKind.UpdateSplinePoints when command.EntityIds.Count != 1 || command.Transform is not null
                || command.Text is not null || command.State is not null || command.Transforms?.Count > 0
                || command.LevelSettings is not null || command.Points is null
                || !IsEditablePath(workspace.Content.Entities.Single(
                    entity => entity.EntityId == command.EntityIds[0])):
                throw new ArgumentException("Path point commands require one editable path and its points.", nameof(command));
            case EditorCommandKind.CreateEntityFromAsset when command.EntityIds.Count != 0
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0 || command.LevelSettings is not null || command.Points is not null
                || command.Placement is null:
                throw new ArgumentException("Asset placement commands require only placement data.", nameof(command));
            case EditorCommandKind.AddSkyShellFromAsset when command.EntityIds.Count != 0
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0 || command.LevelSettings is not null || command.Points is not null
                || command.Placement is not null || command.SkyShellSource is null
                || command.SkyShellSource.ShellIndex < 0:
                throw new ArgumentException("Sky shell add commands require only a source asset and shell index.", nameof(command));
            case EditorCommandKind.UpdateSkyShell when command.EntityIds.Count != 1
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0 || command.LevelSettings is not null || command.Points is not null
                || command.Placement is not null || command.SkyShellUpdate is null
                || command.SkyShellUpdate.InitialRotationRadians is null
                    && command.SkyShellUpdate.AngularVelocityRadiansPerSecond is null
                || workspace.Content.Entities.Single(entity => entity.EntityId == command.EntityIds[0]).SkyShell is null:
                throw new ArgumentException("Sky shell update commands require one sky shell and at least one rotation value.", nameof(command));
            case EditorCommandKind.ReorderSkyShell when command.EntityIds.Count != 1
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0 || command.LevelSettings is not null || command.Points is not null
                || command.Placement is not null || command.DestinationOrder is null or < 0
                || workspace.Content.Entities.Single(entity => entity.EntityId == command.EntityIds[0]).SkyShell is null:
                throw new ArgumentException("Sky shell reorder commands require one sky shell and a destination order.", nameof(command));
            case EditorCommandKind.RemoveInstancedCollisionProxy when command.EntityIds.Count != 1
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0
                || !workspace.Content.Entities.Single(entity => entity.EntityId == command.EntityIds[0])
                    .Asset.IsInstancedCollisionSource():
                throw new ArgumentException("Collision remove commands require one TIE or shrub entity.", nameof(command));
            case EditorCommandKind.SetInstancedCollisionEnabled when command.EntityIds.Count == 0
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0
                || workspace.GetEntities(command.EntityIds).Any(entity =>
                    !entity.Asset.IsInstancedCollisionSource()):
                throw new ArgumentException("Collision toggle commands require TIE or shrub entities.", nameof(command));
            case EditorCommandKind.SetInstancedCollisionRawType when command.EntityIds.Count != 1
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0 || command.InstancedCollisionRawType is null
                || !HasSelectedInstancedCollisionBinding(workspace, command.EntityIds[0]):
                throw new ArgumentException("Collision ID commands require one TIE or shrub with a collision proxy.", nameof(command));
            case EditorCommandKind.SetInstancedCollisionFaceTypes when command.EntityIds.Count != 1
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0 || command.InstancedCollisionProxyAssetId is null
                || command.InstancedCollisionFaceTypes is not { Count: > 0 }
                || command.InstancedCollisionFaceTypes.Count > ForgeProjectValidation.MaxInstancedCollisionFaceTypeOverrides
                || command.InstancedCollisionFaceTypes.Any(value => value is null || value.FaceIndex < 0)
                || command.InstancedCollisionFaceTypes.Select(value => value.FaceIndex).Distinct().Count()
                    != command.InstancedCollisionFaceTypes.Count
                || !HasSelectedInstancedCollisionBinding(
                    workspace, command.EntityIds[0], command.InstancedCollisionProxyAssetId):
                throw new ArgumentException(
                    "Collision face commands require one TIE or shrub, its current proxy, and unique face assignments.",
                    nameof(command));
            case EditorCommandKind.SetEntityReference when command.EntityIds.Count != 1
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0
                || command.ReferenceUpdate is not { } reference
                || reference.FieldKey.Length is 0 or > 128
                || reference.FieldKey.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) || character is '.' or '[' or ']' or '-'))
                || reference.TargetEntityId is { } target && !known.Contains(target):
                throw new ArgumentException(
                    "Reference commands require one owner, a valid field key, and a known target.",
                    nameof(command));
            case EditorCommandKind.SetMobyInstanceProperty when command.EntityIds.Count == 0
                || command.Transform is not null || command.Text is not null || command.State is not null
                || command.Transforms?.Count > 0
                || command.MobyPropertyEdit is not { } mobyEdit
                || mobyEdit.FieldKey.Length is 0 or > 64
                || mobyEdit.ExpectedClassId < 0
                || mobyEdit.FieldKey.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-'))
                || workspace.GetEntities(command.EntityIds).Any(entity =>
                    entity.Source?.ClassId != mobyEdit.ExpectedClassId
                    || _mobyPropertyResolver?.Invoke(entity) is not { } descriptors
                    || !descriptors.Any(descriptor => descriptor.Key == mobyEdit.FieldKey && descriptor.Editable)):
                throw new ArgumentException(
                    "Moby property commands require compatible editable mobys and a valid property value.", nameof(command));
            case EditorCommandKind.ReplaceHudTexture when command.EntityIds.Count != 0
                || HasNonHudCommandData(command)
                || command.HudEdit is not
                {
                    SourceAssetId: not null,
                    SpriteId: null,
                    BankIndex: null,
                    ImageFormat: not null,
                    ImageBytes.Length: > 0,
                }:
                throw new ArgumentException(
                    "HUD replacement commands require a source texture and image.", nameof(command));
            case EditorCommandKind.RemoveHudTextureOverride when command.EntityIds.Count != 0
                || HasNonHudCommandData(command)
                || command.HudEdit is not
                {
                    SourceAssetId: not null,
                    SpriteId: null,
                    BankIndex: null,
                    ImageFormat: null,
                    ImageBytes: null,
                }:
                throw new ArgumentException(
                    "HUD override removal commands require only a source texture.", nameof(command));
            case EditorCommandKind.AddHudIcon when command.EntityIds.Count != 0
                || HasNonHudCommandData(command)
                || command.HudEdit is not
                {
                    SourceAssetId: null,
                    SpriteId: not null,
                    BankIndex: >= 0,
                    ImageFormat: not null,
                    ImageBytes.Length: > 0,
                }:
                throw new ArgumentException(
                    "HUD add commands require a sprite ID, bank, and image.", nameof(command));
            case EditorCommandKind.RemoveHudIcon when command.EntityIds.Count != 0
                || HasNonHudCommandData(command)
                || command.HudEdit is not
                {
                    SourceAssetId: null,
                    SpriteId: not null,
                    BankIndex: null,
                    ImageFormat: null,
                    ImageBytes: null,
                }:
                throw new ArgumentException(
                    "HUD removal commands require only a sprite ID.", nameof(command));
            case EditorCommandKind.ReplaceFxTexture when command.EntityIds.Count != 0
                || HasNonFxCommandData(command)
                || command.FxEdit is not
                {
                    SourceAssetId: not null,
                    Index: null,
                    ImageFormat: not null,
                    ImageBytes.Length: > 0,
                }:
                throw new ArgumentException(
                    "FX replacement commands require a source texture and image.", nameof(command));
            case EditorCommandKind.RemoveFxTextureOverride when command.EntityIds.Count != 0
                || HasNonFxCommandData(command)
                || command.FxEdit is not
                {
                    SourceAssetId: not null,
                    Index: null,
                    ImageFormat: null,
                    ImageBytes: null,
                }:
                throw new ArgumentException(
                    "FX override removal commands require only a source texture.", nameof(command));
            case EditorCommandKind.AddFxTexture when command.EntityIds.Count != 0
                || HasNonFxCommandData(command)
                || command.FxEdit is not
                {
                    SourceAssetId: null,
                    Index: null,
                    ImageFormat: not null,
                    ImageBytes.Length: > 0,
                }:
                throw new ArgumentException(
                    "FX add commands require only an image.", nameof(command));
            case EditorCommandKind.RemoveFxTexture when command.EntityIds.Count != 0
                || HasNonFxCommandData(command)
                || command.FxEdit is not
                {
                    SourceAssetId: null,
                    Index: >= 0,
                    ImageFormat: null,
                    ImageBytes: null,
                }:
                throw new ArgumentException(
                    "FX removal commands require only an appended index.", nameof(command));
            case EditorCommandKind.ImportMobyDexEntry when command.EntityIds.Count != 0
                || HasNonMobyDexCommandData(command)
                || command.MobyDexEdit is not
                {
                    EntryJson.Length: > 0,
                    Game: null,
                    OClass: null,
                }:
                throw new ArgumentException(
                    "MobyDex import commands require only entry JSON.", nameof(command));
            case EditorCommandKind.RemoveMobyDexEntry when command.EntityIds.Count != 0
                || HasNonMobyDexCommandData(command)
                || command.MobyDexEdit is not
                {
                    EntryJson: null,
                    Game: not null,
                    OClass: >= 0 and <= ushort.MaxValue,
                } removal
                || !IsMobyDexGameId(removal.Game)
                || workspace.Content.MobyDex?.Entries.Any(entry =>
                    entry.Game == removal.Game && entry.OClass == removal.OClass) != true:
                throw new ArgumentException(
                    "MobyDex removal commands require only a game ID and OClass.", nameof(command));
            case EditorCommandKind.InitializeMobyPVar when command.EntityIds.Count != 1
                || HasNonMobyDexCommandData(command)
                || command.MobyDexEdit is not null
                || workspace.Content.Entities.Single(value => value.EntityId == command.EntityIds[0]).Layer != "mobys"
                || workspace.Content.MobyPVars?.Entries.Any(value => value.EntityId == command.EntityIds[0]) == true:
                throw new ArgumentException(
                    "PVar initialization commands require one moby without a PVar.", nameof(command));
            case EditorCommandKind.SetMobyPVarField when command.EntityIds.Count != 1
                || HasNonMobyDexCommandData(command)
                || command.MobyDexEdit is not null
                || command.MobyPVarEdit is not { } pvarEdit
                || pvarEdit.FieldPath.Length is 0 or > 256
                || pvarEdit.ExpectedClassId is < 0 or > ushort.MaxValue
                || pvarEdit.ExpectedDatasetId.Length is 0 or > 128
                || pvarEdit.ExpectedDatasetVersion < 1
                || pvarEdit.ExpectedSchemaVersion < 1
                || pvarEdit.ExpectedSchemaFingerprint.Length != 64
                || pvarEdit.ExpectedSchemaFingerprint.Any(character => !char.IsAsciiHexDigitLower(character))
                || pvarEdit.ExpectedStateFingerprint.Length != 64
                || pvarEdit.ExpectedStateFingerprint.Any(character => !char.IsAsciiHexDigitLower(character)):
                throw new ArgumentException(
                    "PVar field commands require one moby and valid guarded field data.", nameof(command));
            case EditorCommandKind.CreateGroup when command.EntityIds.Count != 0
                || string.IsNullOrWhiteSpace(command.Text)
                || command.GroupEdit is not { GroupId: null, DestinationOrder: null }
                || HasNonGroupCommandData(command, allowText: true):
                throw new ArgumentException("Group create commands require only a name.", nameof(command));
            case EditorCommandKind.RenameGroup when command.EntityIds.Count != 0
                || string.IsNullOrWhiteSpace(command.Text)
                || command.GroupEdit is not { GroupId: not null, DestinationOrder: null }
                || HasNonGroupCommandData(command, allowText: true)
                || !workspace.Content.Groups.Any(group => group.GroupId == command.GroupEdit.GroupId):
                throw new ArgumentException("Group rename commands require an existing group and a name.", nameof(command));
            case EditorCommandKind.DeleteGroup when command.EntityIds.Count != 0 || command.Text is not null
                || command.GroupEdit is not { GroupId: not null, DestinationOrder: null }
                || HasNonGroupCommandData(command, allowText: false)
                || !workspace.Content.Groups.Any(group => group.GroupId == command.GroupEdit.GroupId):
                throw new ArgumentException("Group delete commands require only an existing group.", nameof(command));
            case EditorCommandKind.ReorderGroup when command.EntityIds.Count != 0 || command.Text is not null
                || command.GroupEdit is not { GroupId: not null, DestinationOrder: >= 0 }
                || command.GroupEdit.DestinationOrder >= workspace.Content.Groups.Count
                || HasNonGroupCommandData(command, allowText: false)
                || !workspace.Content.Groups.Any(group => group.GroupId == command.GroupEdit.GroupId):
                throw new ArgumentException("Group reorder commands require an existing group and valid order.", nameof(command));
            case EditorCommandKind.AddGroupMembers or EditorCommandKind.RemoveGroupMembers
                when command.EntityIds.Count == 0 || command.Text is not null
                || command.GroupEdit is not { GroupId: not null, DestinationOrder: null }
                || HasNonGroupCommandData(command, allowText: false)
                || !workspace.Content.Groups.Any(group => group.GroupId == command.GroupEdit.GroupId):
                throw new ArgumentException("Group membership commands require an existing group and entities.", nameof(command));
        }
        if (command.Kind is EditorCommandKind.UpdateTransform or EditorCommandKind.UpdateTransforms)
            ValidateTransformCapabilities(command, workspace);
    }

    private static bool HasNonHudCommandData(EditorCommand command) =>
        HasCommonCommandData(command) || command.FxEdit is not null
        || command.MobyDexEdit is not null;

    private static bool HasNonFxCommandData(EditorCommand command) =>
        HasCommonCommandData(command) || command.HudEdit is not null
        || command.MobyDexEdit is not null;

    private static bool HasNonMobyDexCommandData(EditorCommand command) =>
        HasCommonCommandData(command) || command.HudEdit is not null || command.FxEdit is not null
        || command.MobyPropertyEdit is not null;

    private static bool HasNonGroupCommandData(EditorCommand command, bool allowText) =>
        HasCommonCommandData(command, allowText)
        || command.HudEdit is not null || command.FxEdit is not null
        || command.MobyPropertyEdit is not null || command.MobyDexEdit is not null || command.MobyPVarEdit is not null;

    private static bool HasCommonCommandData(EditorCommand command, bool allowText = false) =>
        command.Transform is not null || !allowText && command.Text is not null || command.State is not null
        || command.Transforms?.Count > 0 || command.LevelSettings is not null || command.Points is not null
        || command.Placement is not null || command.SkyShellSource is not null || command.SkyShellUpdate is not null
        || command.DestinationOrder is not null || command.InstancedCollisionEnabled is not null
        || command.InstancedCollisionRawType is not null || command.InstancedCollisionProxyAssetId is not null
        || command.InstancedCollisionFaceTypes is not null || command.ReferenceUpdate is not null
        || command.PaletteOptimization is not null;

    private static bool IsMobyDexGameId(string value) => value.Length is >= 2 and <= 16
        && value[0] is >= 'A' and <= 'Z'
        && value.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '-');

    private static void ValidateCommandId(string commandId)
    {
        if (!Guid.TryParseExact(commandId, "D", out var parsed) || parsed == Guid.Empty
            || parsed.ToString("D") != commandId)
            throw new ArgumentException("Command ID must be a lowercase canonical UUID.", nameof(commandId));
    }

    private static bool HasSelectedInstancedCollisionBinding(
        ForgeProjectWorkspace workspace,
        EntityId entityId,
        AssetId? proxyAssetId = null)
    {
        var entity = workspace.GetEntities([entityId])[0];
        if (!entity.Asset.IsInstancedCollisionSource()) return false;
        var instanceEntityId = entity.InstancedCollisionEnabled == false ? entityId : (EntityId?)null;
        return workspace.Content.InstancedCollisionBindings.Any(binding =>
            binding.SourceAssetId == entity.Asset.Id
            && binding.InstanceEntityId == instanceEntityId
            && (proxyAssetId is null || binding.ProxyAssetId == proxyAssetId));
    }

    private static bool IsEditablePath(ProjectEntity entity) =>
        entity.Geometry?.Spline is not null || entity.Geometry?.GrindPath is not null;

    private void ValidateTransformCapabilities(EditorCommand command, ForgeProjectWorkspace workspace)
    {
        var entities = workspace.Content.Entities.ToDictionary(entity => entity.EntityId);
        var updates = command.Kind == EditorCommandKind.UpdateTransform
            ? [new EditorTransformUpdate(command.EntityIds[0], command.Transform!)]
            : command.Transforms!;
        foreach (var update in updates)
        {
            var entity = entities[update.EntityId];
            var capabilities = TransformCapabilities(entity);
            if (!capabilities.HasFlag(EditorTransformCapabilities.Translate)
                    && update.Transform.Position != entity.Transform.Position
                || !capabilities.HasFlag(EditorTransformCapabilities.Rotate)
                    && update.Transform.Rotation != entity.Transform.Rotation
                || !capabilities.HasFlag(EditorTransformCapabilities.Scale)
                    && update.Transform.Scale != entity.Transform.Scale)
                throw new ArgumentException("Transform command changes an unsupported component.", nameof(command));
        }
    }
}

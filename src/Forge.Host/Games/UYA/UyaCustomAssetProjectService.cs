using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal static class UyaCustomAssetProjectService
{
    public static async Task ExecuteAsync(
        ForgeProjectWorkspace workspace,
        EditorCommand command,
        CancellationToken cancellationToken)
    {
        var edit = command.CustomModelEdit!;
        switch (command.Kind)
        {
            case EditorCommandKind.DeleteEntities:
                _ = await UyaCustomTfragProjectService.DeleteAsync(
                    workspace, command.EntityIds, cancellationToken);
                break;
            case EditorCommandKind.ImportCustomTfrag:
                _ = await UyaCustomTfragProjectService.ImportAsync(workspace, edit.Import!, cancellationToken);
                break;
            case EditorCommandKind.ImportCustomTie:
                _ = await UyaCustomTieProjectService.ImportAsync(workspace, edit.Import!, cancellationToken);
                break;
            case EditorCommandKind.ReimportCustomTie:
                _ = await UyaCustomTieProjectService.ReimportAsync(
                    workspace, edit.ModelAssetId!.Value, edit.Import!, command.EntityIds, cancellationToken);
                break;
            case EditorCommandKind.DeleteCustomTie:
                await UyaCustomTieProjectService.DeleteAsync(
                    workspace, edit.ModelAssetId!.Value, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(command), command.Kind, "Unknown UYA custom asset command.");
        }
    }
}

import { useCallback, useState } from 'react';

import type { EditorCommand, EditorLayoutAction, EditorSnapshot, ForgeAction } from '../../types/ForgeApi.js';
import { errorMessage } from '../../utils/Errors.ts';
import { applyEditorCommandResult } from '../../utils/EditorCommandResult.ts';

import { forgeLog } from '../log/LogStore.ts';
import { useLogReporter } from '../log/UseLog.ts';

export function useForgeActions(
  setSetupOpened: (value: boolean) => void,
  setSettingsOpened: (value: boolean) => void,
  openSessionLog: () => void,
) {
  const [activeProject, setActiveProject] = useState<EditorSnapshot>();
  const reportError = useLogReporter('Project');
  const [isSaving, setIsSaving] = useState(false);
  const [hubAction, setHubAction] = useState<{ id: number; action: 'newProject' | 'openProject' }>();
  const [layoutAction, setLayoutAction] = useState<{ id: number; action: EditorLayoutAction }>();
  const [contentImportOpened, setContentImportOpened] = useState(false);

  const handleForgeAction = useCallback((action: ForgeAction) => {
    if (action === 'setup') setSetupOpened(true);
    else if (action === 'settings') setSettingsOpened(true);
    else if (action === 'checkUpdates') {
      void window.forge.checkForUpdates().catch((error) => reportError(errorMessage(error)));
    }
    else if (action === 'reloadMobyDex') {
      void window.forge.reloadMobyDexDataset()
        .then(async (result) => {
          if (!result) return;
          if (activeProject) setActiveProject(await window.forge.getEditorSnapshot());
        })
        .catch((error) => reportError(errorMessage(error)));
    }
    else if (action === 'saveProject') {
      if (activeProject && !isSaving) {
        setIsSaving(true);
        void window.forge.saveEditorProject()
          .then((snapshot) => {
            setActiveProject(snapshot);
            forgeLog.write('Project', 'success', `${snapshot.projectName} saved.`);
          })
          .catch((error) => reportError(errorMessage(error)))
          .finally(() => setIsSaving(false));
      }
    }
    else if (action === 'importMapContent') {
      if (activeProject?.target.game === 'UYA') setContentImportOpened(true);
    }
    else if (action === 'undoEditor' || action === 'redoEditor') {
      if (activeProject && (action === 'undoEditor' ? activeProject.canUndo : activeProject.canRedo)) {
        void window.forge.executeEditorCommand({
          id: crypto.randomUUID(),
          kind: action === 'undoEditor' ? 'undo' : 'redo',
          entityIds: [],
        }).then((result) => setActiveProject((project) => project
          ? applyEditorCommandResult(project, result)
          : project)).catch((error) => reportError(errorMessage(error)));
      }
    }
    else if (isEditorEntityAction(action)) {
      if (!activeProject) return;
      const selectedIds = new Set(activeProject.selection);
      const selected = activeProject.entities.filter((entity) => selectedIds.has(entity.id));
      if ((action === 'deleteEntities' || action === 'duplicateEntities')
        && (selected.length === 0 || selected.some((entity) => entity.state.locked))) return;
      if (action === 'copyEntities' && activeProject.selection.length === 0) return;
      if (action === 'pasteEntities' && !activeProject.canPaste) return;
      const entityIds = action === 'pasteEntities' ? [] : activeProject.selection;
      const command = { id: crypto.randomUUID(), kind: action, entityIds } as EditorCommand;
      void window.forge.executeEditorCommand(command)
        .then((result) => setActiveProject((project) => project
          ? applyEditorCommandResult(project, result)
          : project))
        .catch((error) => reportError(errorMessage(error)));
    }
    else if (action === 'showDiagnostics' && !activeProject) openSessionLog();
    else if (isEditorLayoutAction(action)) {
      if (activeProject) setLayoutAction({ id: Date.now(), action });
    }
    else if (action === 'projects') {
      void (activeProject ? window.forge.closeEditorProject() : Promise.resolve()).then(() => {
        setHubAction(undefined);
        setLayoutAction(undefined);
        setActiveProject(undefined);
        if (activeProject) forgeLog.write('Project', 'info', `Closed ${activeProject.projectName}.`);
      }).catch((error) => reportError(errorMessage(error)));
    }
    else {
      void (activeProject ? window.forge.closeEditorProject() : Promise.resolve()).then(() => {
        setActiveProject(undefined);
        setLayoutAction(undefined);
        setHubAction({ id: Date.now(), action });
      }).catch((error) => reportError(errorMessage(error)));
    }
  }, [activeProject, isSaving, setSettingsOpened, setSetupOpened, openSessionLog]);

  const openProject = useCallback((path: string) => {
    void window.forge.openEditorProject(path).then((snapshot) => {
      forgeLog.clear();
      forgeLog.write('Project', 'success', `Opened ${snapshot.projectName}.`);
      setHubAction(undefined);
      setLayoutAction(undefined);
      setActiveProject(snapshot);
    }).catch((error) => reportError(errorMessage(error)));
  }, []);

  return {
    activeProject,
    hubAction,
    isSaving,
    layoutAction,
    contentImportOpened,
    handleForgeAction,
    openProject,
    setActiveProject,
    closeContentImport: () => setContentImportOpened(false),
  };
}

function isEditorLayoutAction(action: ForgeAction): action is EditorLayoutAction {
  return ['resetLayout', 'showViewport', 'showSceneTree', 'showGroups', 'showProperties', 'showLevelSettings',
    'showReferences', 'showDiagnostics', 'showBuild', 'showAssetExplorer', 'showCustomAssets', 'showAssetPreview', 'showHudBank',
    'showFxTextures', 'showSounds', 'showImports'].includes(action);
}

function isEditorEntityAction(action: ForgeAction): action is Extract<ForgeAction,
  'deleteEntities' | 'duplicateEntities' | 'copyEntities' | 'pasteEntities'> {
  return ['deleteEntities', 'duplicateEntities', 'copyEntities', 'pasteEntities'].includes(action);
}

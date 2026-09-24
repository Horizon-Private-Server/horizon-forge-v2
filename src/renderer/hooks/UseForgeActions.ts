import { useCallback, useState } from 'react';

import type { EditorCommand, EditorLayoutAction, EditorSnapshot, ForgeAction } from '../../types/ForgeApi.js';
import { errorMessage } from '../../utils/Errors.ts';

export function useForgeActions(setSetupOpened: (value: boolean) => void, setSettingsOpened: (value: boolean) => void) {
  const [activeProject, setActiveProject] = useState<EditorSnapshot>();
  const [editorError, setEditorError] = useState<string>();
  const [hubAction, setHubAction] = useState<{ id: number; action: 'newProject' | 'openProject' }>();
  const [layoutAction, setLayoutAction] = useState<{ id: number; action: EditorLayoutAction }>();

  const handleForgeAction = useCallback((action: ForgeAction) => {
    if (action === 'setup') setSetupOpened(true);
    else if (action === 'settings') setSettingsOpened(true);
    else if (action === 'saveProject') {
      if (activeProject) void window.forge.saveEditorProject()
        .then(setActiveProject)
        .catch((error) => setEditorError(errorMessage(error)));
    }
    else if (action === 'undoEditor' || action === 'redoEditor') {
      if (activeProject && (action === 'undoEditor' ? activeProject.canUndo : activeProject.canRedo)) {
        void window.forge.executeEditorCommand({
          id: crypto.randomUUID(),
          kind: action === 'undoEditor' ? 'undo' : 'redo',
          entityIds: [],
        }).then(setActiveProject).catch((error) => setEditorError(errorMessage(error)));
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
        .then(setActiveProject)
        .catch((error) => setEditorError(errorMessage(error)));
    }
    else if (isEditorLayoutAction(action)) {
      if (activeProject) setLayoutAction({ id: Date.now(), action });
    }
    else if (action === 'projects') {
      void (activeProject ? window.forge.closeEditorProject() : Promise.resolve()).then(() => {
        setHubAction(undefined);
        setLayoutAction(undefined);
        setActiveProject(undefined);
      }).catch((error) => setEditorError(errorMessage(error)));
    }
    else {
      void (activeProject ? window.forge.closeEditorProject() : Promise.resolve()).then(() => {
        setActiveProject(undefined);
        setLayoutAction(undefined);
        setHubAction({ id: Date.now(), action });
      }).catch((error) => setEditorError(errorMessage(error)));
    }
  }, [activeProject, setSettingsOpened, setSetupOpened]);

  const openProject = useCallback((path: string) => {
    void window.forge.openEditorProject(path).then((snapshot) => {
      setEditorError(undefined);
      setHubAction(undefined);
      setLayoutAction(undefined);
      setActiveProject(snapshot);
    }).catch((error) => setEditorError(errorMessage(error)));
  }, []);

  return {
    activeProject,
    editorError,
    hubAction,
    layoutAction,
    handleForgeAction,
    openProject,
    setActiveProject,
    clearEditorError: () => setEditorError(undefined),
  };
}

function isEditorLayoutAction(action: ForgeAction): action is EditorLayoutAction {
  return ['resetLayout', 'showViewport', 'showSceneTree', 'showProperties', 'showDiagnostics', 'showBuild'].includes(action);
}

function isEditorEntityAction(action: ForgeAction): action is Extract<ForgeAction,
  'deleteEntities' | 'duplicateEntities' | 'copyEntities' | 'pasteEntities'> {
  return ['deleteEntities', 'duplicateEntities', 'copyEntities', 'pasteEntities'].includes(action);
}

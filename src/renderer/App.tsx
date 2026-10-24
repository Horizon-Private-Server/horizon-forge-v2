import { FloppyDiskIcon } from '@phosphor-icons/react/dist/csr/FloppyDisk';
import { FolderOpenIcon } from '@phosphor-icons/react/dist/csr/FolderOpen';
import { ActionIcon, Badge, Drawer, Group, MantineProvider, Text, Tooltip } from '@mantine/core';
import { useCallback, useEffect, useMemo, useState } from 'react';

import type { KeybindingMap } from '../types/Keybindings.js';
import type { ForgeHostStatus, UiSize } from '../types/ForgeApi.js';
import { parseKeybindingOverrides, resolveKeybindings } from '../utils/Keybindings.ts';
import {
  DEFAULT_SCENE_TREE_COLORS,
  readSceneTreeColors,
  sceneTreeColorVariables,
} from '../utils/SceneTreeColors.ts';
import type { SceneTreeColors } from '../types/SceneTree.js';
import type { CollisionVisualization } from '../types/CollisionVisualization.js';
import {
  DEFAULT_UYA_COLLISION_VISUALIZATION,
  readUyaCollisionVisualization,
} from '../utils/UyaCollisionVisualization.ts';
import { EditorWorkspace } from './editor/EditorWorkspace.tsx';
import { MapContentImportDialog } from './editor/MapContentImportDialog.tsx';
import { useForgeActions } from './hooks/UseForgeActions.ts';
import { useKeyboardContext } from './hooks/UseKeyboardContext.ts';
import { useNotifications } from './hooks/UseNotifications.ts';
import { useUpdateLifecycle } from './hooks/UseUpdateLifecycle.ts';
import { ProjectHub } from './projects/ProjectHub.tsx';
import { SettingsModal } from './settings/SettingsModal.tsx';
import { SetupWizard } from './setup/SetupWizard.tsx';
import { ForgeMenuBar } from './shell/ForgeMenuBar.tsx';
import { createForgeTheme } from './theme.ts';
import { UpdateStatusMenu } from './updates/UpdateStatusMenu.tsx';
import { errorMessage } from '../utils/Errors.ts';
import { isUiSize } from '../utils/UiSize.ts';
import { DEFAULT_SELECTION_COLOR, readSelectionColor } from '../utils/SelectionVisualization.ts';

import { LogPanel } from './log/LogPanel.tsx';
import { forgeLog } from './log/LogStore.ts';
import { LogButton } from './log/LogButton.tsx';
import { LogNotificationsContext } from './log/LogNotificationsContext.ts';

export function App() {
  const [uiSize, setUiSize] = useState<UiSize>('xs');
  const theme = useMemo(() => createForgeTheme(uiSize), [uiSize]);
  return <MantineProvider defaultColorScheme="dark" theme={theme}>
    <ForgeApp uiSize={uiSize} onUiSizeChange={setUiSize} />
  </MantineProvider>;
}

function ForgeApp({ uiSize, onUiSizeChange }: { uiSize: UiSize; onUiSizeChange(value: UiSize): void }) {
  const [logOpened, setLogOpened] = useState(false);
  const openSessionLog = useCallback(() => setLogOpened(true), []);
  const [hostStatus, setHostStatus] = useState<ForgeHostStatus>();
  const [settingsOpened, setSettingsOpened] = useState(false);
  const [showViewportStats, setShowViewportStats] = useState(true);
  const [selectionColor, setSelectionColor] = useState(DEFAULT_SELECTION_COLOR);
  const [sceneTreeColors, setSceneTreeColors] = useState<SceneTreeColors>(DEFAULT_SCENE_TREE_COLORS);
  const [collisionVisualization, setCollisionVisualization] = useState<CollisionVisualization>(
    DEFAULT_UYA_COLLISION_VISUALIZATION,
  );
  const [keybindings, setKeybindings] = useState<KeybindingMap>(() => resolveKeybindings({}));
  const [setupOpened, setSetupOpened] = useState(false);
  const [hubRefresh, setHubRefresh] = useState(0);
  const [contentImportRevision, setContentImportRevision] = useState(0);
  const {
    activeProject,
    hubAction,
    layoutAction,
    contentImportOpened,
    handleForgeAction,
    isSaving,
    openProject,
    setActiveProject,
    closeContentImport,
  } = useForgeActions(setSetupOpened, setSettingsOpened, openSessionLog);
  useKeyboardContext(keybindings, handleForgeAction);
  const notificationCenter = useNotifications();
  const updateLifecycle = useUpdateLifecycle();

  useEffect(() => {
    let mounted = true;
    void window.forge.getHostStatus()
      .then((status) => {
        if (mounted) setHostStatus(status);
      })
      .catch((cause) => {
        if (mounted) {
          setHostStatus(undefined);
          forgeLog.write('Host', 'error', errorMessage(cause));
        }
      });
    return () => {
      mounted = false;
    };
  }, []);

  useEffect(() => {
    void window.forge.getSettings().then((snapshot) => {
      setShowViewportStats(snapshot.entries.find((entry) => entry.key === 'ui.showViewportStats')?.value === true);
      setSelectionColor(readSelectionColor(snapshot.entries));
      setSceneTreeColors(readSceneTreeColors(snapshot.entries));
      setCollisionVisualization(readUyaCollisionVisualization(snapshot.entries));
      setKeybindings(resolveKeybindings(parseKeybindingOverrides(
        snapshot.entries.find((entry) => entry.key === 'keybindings.overrides')?.value,
      )));
      const storedSize = snapshot.entries.find((entry) => entry.key === 'ui.componentSize')?.value;
      if (isUiSize(storedSize)) onUiSizeChange(storedSize);
    }).catch((cause) => forgeLog.write('Settings', 'error', errorMessage(cause)));
  }, []);

  useEffect(() => {
    void window.forge.getSetupState().then((state) => setSetupOpened(state.required))
      .catch((cause) => forgeLog.write('Setup', 'error', errorMessage(cause)));
  }, []);

  useEffect(() => window.forge.onForgeAction(handleForgeAction), [handleForgeAction]);

  return (
    <LogNotificationsContext.Provider value={notificationCenter}><main className="app-shell" data-ui-size={uiSize} style={sceneTreeColorVariables(sceneTreeColors)}>
      <ForgeMenuBar keybindings={keybindings} project={activeProject} onAction={handleForgeAction} />
      <header className="app-header">
        <Group className="app-header-content" wrap="nowrap">
          {activeProject && <Group className="project-ribbon" wrap="nowrap">
            <Tooltip label={isSaving ? 'Saving project…' : 'Save project'}>
              <ActionIcon
                className="ribbon-action"
                aria-busy={isSaving}
                aria-label={isSaving ? 'Saving project' : 'Save project'}
                disabled={isSaving}
                onClick={() => handleForgeAction('saveProject')}
              >
                <FloppyDiskIcon weight="bold" />
              </ActionIcon>
            </Tooltip>
            <Tooltip label="Return to projects">
              <ActionIcon
                className="ribbon-action"
                aria-label="Return to projects"
                disabled={isSaving}
                variant="default"
                onClick={() => handleForgeAction('projects')}
              >
                <FolderOpenIcon weight="bold" />
              </ActionIcon>
            </Tooltip>
            {activeProject.isDirty && <Badge
              aria-label="Project has unsaved changes"
              color="yellow"
              variant="light"
            >Unsaved</Badge>}
            <Text
              className="project-name"
              aria-label={`Project ${activeProject.projectName}`}
              fw={600}
              size="sm"
              title={activeProject.projectName}
            >
              {activeProject.projectName}
            </Text>
          </Group>}
          <Group className="app-header-status" gap="xs" wrap="nowrap">
            <UpdateStatusMenu snapshot={updateLifecycle.snapshot} onAction={updateLifecycle.runAction} />
            <LogButton onOpen={() => activeProject ? handleForgeAction('showDiagnostics') : setLogOpened(true)} />
          </Group>
        </Group>
      </header>
      <div className="app-content">
        {activeProject
          ? <EditorWorkspace
            project={activeProject}
            keybindings={keybindings}
            sceneTreeColors={sceneTreeColors}
            selectionColor={selectionColor}
            collisionVisualization={collisionVisualization}
            hostStatus={hostStatus}
            layoutAction={layoutAction}
            renderRevision={contentImportRevision}
            showViewportStats={showViewportStats}
            onProjectChange={setActiveProject}
          />
          : <ProjectHub
            hostStatus={hostStatus}
            requestedAction={hubAction}
            refreshToken={hubRefresh}
            onOpen={(project) => openProject(project.path)}
            onOpenSetup={() => setSetupOpened(true)}
          />}
      </div>
      <Drawer opened={logOpened} onClose={() => setLogOpened(false)} title="Log" position="bottom" size="45%"
        styles={{ content: { display: 'flex', flexDirection: 'column' },
          header: { flexShrink: 0 }, body: { flex: 1, minHeight: 0, padding: 0 } }}>
        <LogPanel visible={logOpened} />
      </Drawer>
      <SettingsModal
        opened={settingsOpened}
        onClose={() => setSettingsOpened(false)}
        onKeybindingsChange={setKeybindings}
        sceneTreeColors={sceneTreeColors}
        onSceneTreeColorsChange={setSceneTreeColors}
        selectionColor={selectionColor}
        onSelectionColorChange={setSelectionColor}
        collisionVisualization={collisionVisualization}
        onCollisionVisualizationChange={setCollisionVisualization}
        onViewportStatsChange={setShowViewportStats}
        uiSize={uiSize}
        onUiSizeChange={onUiSizeChange}
      />
      <SetupWizard opened={setupOpened} onClose={() => {
        setSetupOpened(false);
        setHubRefresh((value) => value + 1);
      }} />
      {activeProject && <MapContentImportDialog
        opened={contentImportOpened}
        project={activeProject}
        onClose={closeContentImport}
        onApplied={(snapshot) => {
          setActiveProject(snapshot);
          setContentImportRevision((value) => value + 1);
        }}
      />}
    </main></LogNotificationsContext.Provider>
  );
}

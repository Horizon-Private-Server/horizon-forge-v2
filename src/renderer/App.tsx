import { Alert, Badge, Button, Group, Text, Title } from '@mantine/core';
import { useEffect, useState } from 'react';

import type { KeybindingMap } from '../types/Keybindings.js';
import type { ForgeHostStatus } from '../types/ForgeApi.js';
import { parseKeybindingOverrides, resolveKeybindings } from '../utils/Keybindings.ts';
import { EditorWorkspace } from './editor/EditorWorkspace.tsx';
import { useForgeActions } from './hooks/UseForgeActions.ts';
import { useKeyboardContext } from './hooks/UseKeyboardContext.ts';
import { useNotifications } from './hooks/UseNotifications.ts';
import { ProjectHub } from './projects/ProjectHub.tsx';
import { SettingsModal } from './settings/SettingsModal.tsx';
import { SetupWizard } from './setup/SetupWizard.tsx';
import { ForgeMenuBar } from './shell/ForgeMenuBar.tsx';
import { NotificationMenu } from './shell/NotificationMenu.tsx';

export function App() {
  const [hostStatus, setHostStatus] = useState<ForgeHostStatus>();
  const [settingsOpened, setSettingsOpened] = useState(false);
  const [showViewportStats, setShowViewportStats] = useState(true);
  const [keybindings, setKeybindings] = useState<KeybindingMap>(() => resolveKeybindings({}));
  const [setupOpened, setSetupOpened] = useState(false);
  const [hubRefresh, setHubRefresh] = useState(0);
  const {
    activeProject,
    editorError,
    hubAction,
    layoutAction,
    handleForgeAction,
    openProject,
    setActiveProject,
    clearEditorError,
  } = useForgeActions(setSetupOpened, setSettingsOpened);
  useKeyboardContext(keybindings, handleForgeAction);
  const notificationCenter = useNotifications();

  useEffect(() => {
    let mounted = true;
    void window.forge.getHostStatus()
      .then((status) => {
        if (mounted) setHostStatus(status);
      })
      .catch(() => {
        if (mounted) setHostStatus(undefined);
      });
    return () => {
      mounted = false;
    };
  }, []);

  useEffect(() => {
    void window.forge.getSettings().then((snapshot) => {
      setShowViewportStats(snapshot.entries.find((entry) => entry.key === 'ui.showViewportStats')?.value === true);
      setKeybindings(resolveKeybindings(parseKeybindingOverrides(
        snapshot.entries.find((entry) => entry.key === 'keybindings.overrides')?.value,
      )));
    }).catch(() => undefined);
  }, []);

  useEffect(() => {
    void window.forge.getSetupState().then((state) => setSetupOpened(state.required));
  }, []);

  useEffect(() => window.forge.onForgeAction(handleForgeAction), [handleForgeAction]);

  return (
    <main className="app-shell">
      <ForgeMenuBar keybindings={keybindings} project={activeProject} onAction={handleForgeAction} />
      <header className="app-header">
        <Group justify="space-between" wrap="nowrap">
          <div>
            <Title order={4}>Horizon Forge</Title>
            <Text c="dimmed" size="xs">Ratchet &amp; Clank map editor</Text>
          </div>
          <Group>
            <NotificationMenu
              notifications={notificationCenter.notifications}
              onDismiss={notificationCenter.dismiss}
              onAction={notificationCenter.runAction}
            />
            {activeProject && <>
              <Text size="sm">{activeProject.projectName}</Text>
              {activeProject.isDirty && <Badge color="yellow" variant="light">Unsaved</Badge>}
              <Button onClick={() => handleForgeAction('saveProject')}>Save</Button>
              <Button variant="default" onClick={() => handleForgeAction('projects')}>Projects</Button>
            </>}
            <Badge color={hostStatus ? 'teal' : 'yellow'} variant="light">
              {hostStatus ? `Host ${hostStatus.hostVersion.split('+')[0]} · ${hostStatus.supportedGames.join(', ')}` : 'Host connecting'}
            </Badge>
          </Group>
        </Group>
      </header>
      <div className="app-content">
        {editorError && <Alert color="red" withCloseButton onClose={clearEditorError}>{editorError}</Alert>}
        {activeProject
          ? <EditorWorkspace
            project={activeProject}
            keybindings={keybindings}
            hostStatus={hostStatus}
            layoutAction={layoutAction}
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
      <SettingsModal
        opened={settingsOpened}
        onClose={() => setSettingsOpened(false)}
        onKeybindingsChange={setKeybindings}
        onViewportStatsChange={setShowViewportStats}
      />
      <SetupWizard opened={setupOpened} onClose={() => {
        setSetupOpened(false);
        setHubRefresh((value) => value + 1);
      }} />
    </main>
  );
}

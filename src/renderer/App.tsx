import { Alert, Badge, Button, Group, Text, Title } from '@mantine/core';
import { useEffect, useState } from 'react';

import type { EditorLayoutAction, EditorSnapshot, ForgeAction, ForgeHostStatus } from '../types/ForgeApi.js';
import { errorMessage } from '../utils/Errors.js';
import { EditorWorkspace } from './editor/EditorWorkspace.tsx';
import { ProjectHub } from './projects/ProjectHub.tsx';
import { SettingsModal } from './settings/SettingsModal.tsx';
import { SetupWizard } from './setup/SetupWizard.tsx';

export function App() {
  const [hostStatus, setHostStatus] = useState<ForgeHostStatus>();
  const [settingsOpened, setSettingsOpened] = useState(false);
  const [setupOpened, setSetupOpened] = useState(false);
  const [activeProject, setActiveProject] = useState<EditorSnapshot>();
  const [editorError, setEditorError] = useState<string>();
  const [hubRefresh, setHubRefresh] = useState(0);
  const [hubAction, setHubAction] = useState<{ id: number; action: 'newProject' | 'openProject' }>();
  const [layoutAction, setLayoutAction] = useState<{ id: number; action: EditorLayoutAction }>();

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
    void window.forge.getSetupState().then((state) => setSetupOpened(state.required));
    return window.forge.onForgeAction((action: ForgeAction) => {
      if (action === 'setup') setSetupOpened(true);
      else if (action === 'settings') setSettingsOpened(true);
      else if (action === 'saveProject') {
        if (activeProject) {
          void window.forge.saveEditorProject()
            .then(setActiveProject)
            .catch((error) => setEditorError(errorMessage(error)));
        }
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
    });
  }, [activeProject]);

  return (
    <main className="app-shell">
      <header className="app-header">
        <Group justify="space-between" wrap="nowrap">
          <div>
            <Title order={4}>Horizon Forge</Title>
            <Text c="dimmed" size="xs">Ratchet &amp; Clank map editor</Text>
          </div>
          <Group>
            {activeProject && <>
              <Text size="sm">{activeProject.projectName}</Text>
              {activeProject.isDirty && <Badge color="yellow" variant="light">Unsaved</Badge>}
              <Button onClick={() => void window.forge.saveEditorProject()
                .then(setActiveProject)
                .catch((error) => setEditorError(errorMessage(error)))}>Save</Button>
              <Button variant="default" onClick={() => {
                void window.forge.closeEditorProject().then(() => {
                  setHubAction(undefined);
                  setLayoutAction(undefined);
                  setActiveProject(undefined);
                }).catch((error) => setEditorError(errorMessage(error)));
              }}>Projects</Button>
            </>}
            <Badge color={hostStatus ? 'teal' : 'yellow'} variant="light">
              {hostStatus ? `Host ${hostStatus.hostVersion.split('+')[0]} · ${hostStatus.supportedGames.join(', ')}` : 'Host connecting'}
            </Badge>
          </Group>
        </Group>
      </header>
      {editorError && <Alert color="red" withCloseButton onClose={() => setEditorError(undefined)}>{editorError}</Alert>}
      {activeProject
        ? <EditorWorkspace project={activeProject} layoutAction={layoutAction} />
        : <ProjectHub
          hostStatus={hostStatus}
          requestedAction={hubAction}
          refreshToken={hubRefresh}
          onOpen={(project) => void window.forge.openEditorProject(project.path).then((snapshot) => {
            setEditorError(undefined);
            setHubAction(undefined);
            setLayoutAction(undefined);
            setActiveProject(snapshot);
          }).catch((error) => setEditorError(errorMessage(error)))}
          onOpenSetup={() => setSetupOpened(true)}
        />}
      <SettingsModal opened={settingsOpened} onClose={() => setSettingsOpened(false)} />
      <SetupWizard opened={setupOpened} onClose={() => {
        setSetupOpened(false);
        setHubRefresh((value) => value + 1);
      }} />
    </main>
  );
}

function isEditorLayoutAction(action: ForgeAction): action is EditorLayoutAction {
  return ['resetLayout', 'showViewport', 'showSceneTree', 'showProperties', 'showDiagnostics'].includes(action);
}

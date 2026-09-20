import { Badge, Button, Group, Text, Title } from '@mantine/core';
import { useEffect, useState } from 'react';

import type { ForgeAction, ForgeHostStatus, ForgeProjectDescriptor } from '../types/ForgeApi.js';
import { ProjectHub } from './ProjectHub.tsx';
import { SceneViewport } from './SceneViewport.tsx';
import { SetupWizard } from './SetupWizard.tsx';
import { SettingsModal } from './SettingsModal.tsx';

export function App() {
  const [hostStatus, setHostStatus] = useState<ForgeHostStatus>();
  const [settingsOpened, setSettingsOpened] = useState(false);
  const [setupOpened, setSetupOpened] = useState(false);
  const [activeProject, setActiveProject] = useState<ForgeProjectDescriptor>();
  const [hubRefresh, setHubRefresh] = useState(0);
  const [hubAction, setHubAction] = useState<{ id: number; action: 'newProject' | 'openProject' }>();

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
      else if (action === 'projects') {
        setHubAction(undefined);
        setActiveProject(undefined);
      }
      else {
        setActiveProject(undefined);
        setHubAction({ id: Date.now(), action });
      }
    });
  }, []);

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
              <Text size="sm">{activeProject.name}</Text>
              {activeProject.isDirty && <Badge color="yellow" variant="light">Unsaved</Badge>}
              <Button variant="default" onClick={() => {
                setHubAction(undefined);
                setActiveProject(undefined);
              }}>Projects</Button>
            </>}
            <Badge color={hostStatus ? 'teal' : 'yellow'} variant="light">
              {hostStatus ? `Host ${hostStatus.hostVersion.split('+')[0]} · ${hostStatus.supportedGames.join(', ')}` : 'Host connecting'}
            </Badge>
          </Group>
        </Group>
      </header>
      {activeProject
        ? <SceneViewport />
        : <ProjectHub
          hostStatus={hostStatus}
          requestedAction={hubAction}
          refreshToken={hubRefresh}
          onOpen={(project) => {
            setHubAction(undefined);
            setActiveProject(project);
          }}
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

import { Badge, Group, Text, Title } from '@mantine/core';
import { useEffect, useState } from 'react';

import type { ForgeHostStatus } from '../types/ForgeApi.js';
import { SceneViewport } from './SceneViewport.tsx';
import { SetupWizard } from './SetupWizard.tsx';
import { SettingsModal } from './SettingsModal.tsx';

export function App() {
  const [hostStatus, setHostStatus] = useState<ForgeHostStatus>();
  const [settingsOpened, setSettingsOpened] = useState(false);
  const [setupOpened, setSetupOpened] = useState(false);

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
    return window.forge.onOpenDialog((dialog) => {
      if (dialog === 'setup') setSetupOpened(true);
      else setSettingsOpened(true);
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
            <Badge color={hostStatus ? 'teal' : 'yellow'} variant="light">
              {hostStatus ? `Host ${hostStatus.hostVersion.split('+')[0]} · ${hostStatus.supportedGames.join(', ')}` : 'Host connecting'}
            </Badge>
          </Group>
        </Group>
      </header>
      <SceneViewport />
      <SettingsModal opened={settingsOpened} onClose={() => setSettingsOpened(false)} />
      <SetupWizard opened={setupOpened} onClose={() => setSetupOpened(false)} />
    </main>
  );
}

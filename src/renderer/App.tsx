import { Badge, Group, Text, Title } from '@mantine/core';
import { useEffect, useState } from 'react';

import { SceneViewport } from './SceneViewport.tsx';

export function App() {
  const [hostStatus, setHostStatus] = useState<ForgeHostStatus>();

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

  return (
    <main className="app-shell">
      <header className="app-header">
        <Group justify="space-between" wrap="nowrap">
          <div>
            <Title order={3}>Horizon Forge</Title>
            <Text c="dimmed" size="sm">Ratchet &amp; Clank map editor</Text>
          </div>
          <Badge color={hostStatus ? 'teal' : 'yellow'} variant="light">
            {hostStatus ? `Host ${hostStatus.hostVersion.split('+')[0]} · ${hostStatus.supportedGames.join(', ')}` : 'Host connecting'}
          </Badge>
        </Group>
      </header>
      <SceneViewport />
    </main>
  );
}

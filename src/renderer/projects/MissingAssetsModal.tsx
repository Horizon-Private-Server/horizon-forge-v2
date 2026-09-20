import { Alert, Button, Code, Group, Modal, Stack, Text } from '@mantine/core';
import { useState } from 'react';

import type { ForgeProjectDescriptor } from '../../types/ForgeApi.js';
import { errorMessage } from '../../utils/Errors.ts';

interface MissingAssetsModalProps {
  project: ForgeProjectDescriptor;
  onOpenSetup(): void;
  onRepaired(): void;
}

export function MissingAssetsModal({ project, onOpenSetup, onRepaired }: MissingAssetsModalProps) {
  const [opened, setOpened] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const repairable = project.missingAssets.some((asset) => asset.repairable);
  const omitted = project.missingAssetCount - project.missingAssets.reduce((sum, asset) => sum + asset.entityCount, 0);

  async function repair() {
    try {
      setBusy(true);
      setError(undefined);
      await window.forge.repairForgeProjectAssets(project.path);
      setOpened(false);
      onRepaired();
    } catch (cause) {
      setError(errorMessage(cause));
    } finally {
      setBusy(false);
    }
  }

  return <>
    <Button variant="subtle" color="red" onClick={() => setOpened(true)}>Assets…</Button>
    <Modal opened={opened} onClose={() => setOpened(false)} title="Missing project assets" size="lg">
      <Stack>
        {error && <Alert color="red" title="Repair could not continue">{error}</Alert>}
        {project.missingAssets.map((asset) => (
          <Stack key={asset.id} gap={2}>
            <Text size="sm" fw={600}>{asset.kind} · {asset.entityCount} {asset.entityCount === 1 ? 'entity' : 'entities'}</Text>
            <Code>{asset.id}</Code>
            {asset.provenance.map((value) => <Text c="dimmed" size="xs" key={value}>{value}</Text>)}
            {!asset.repairable && <Text c="yellow" size="xs">Restore this project-owned asset from the project backup.</Text>}
          </Stack>
        ))}
        {omitted > 0 && <Alert color="yellow" title="Base instances were omitted">
          {omitted} source instances had no catalog asset when this project was created. Re-import the catalog, then recreate the project to include them.
        </Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onOpenSetup}>Open Setup</Button>
          {busy && <Button variant="default" onClick={() => void window.forge.cancelSetupOperation()}>Cancel repair</Button>}
          <Button onClick={() => void repair()} loading={busy} disabled={!repairable}>Repair from clean ISO</Button>
        </Group>
      </Stack>
    </Modal>
  </>;
}

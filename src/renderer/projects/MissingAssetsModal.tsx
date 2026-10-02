import { Alert, Button, Code, Group, Modal, Stack, Text } from '@mantine/core';
import { useState } from 'react';

import type { ForgeProjectDescriptor } from '../../types/ForgeApi.js';
import { errorMessage } from '../../utils/Errors.ts';

interface MissingAssetsModalProps {
  project: ForgeProjectDescriptor;
  opened: boolean;
  onClose(): void;
  onContinue(): void;
  onOpenSetup(): void;
  onRepaired(project: ForgeProjectDescriptor): void;
}

export function MissingAssetsModal({
  project,
  opened,
  onClose,
  onContinue,
  onOpenSetup,
  onRepaired,
}: MissingAssetsModalProps) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const repairable = project.missingAssets.some((asset) => asset.repairable);
  const omitted = project.missingAssetCount - project.missingAssets.reduce((sum, asset) => sum + asset.entityCount, 0);

  async function repair() {
    try {
      setBusy(true);
      setError(undefined);
      onRepaired(await window.forge.repairForgeProjectAssets(project.path));
    } catch (cause) {
      setError(errorMessage(cause));
    } finally {
      setBusy(false);
    }
  }

  return <Modal opened={opened} onClose={onClose} title="Missing project assets" size="lg">
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
          <Button variant="default" onClick={onContinue}>Open anyway</Button>
          {busy && <Button variant="default" onClick={() => void window.forge.cancelSetupOperation()}>Cancel repair</Button>}
          <Button onClick={() => void repair()} loading={busy} disabled={!repairable}>Repair from clean ISO</Button>
        </Group>
      </Stack>
    </Modal>;
}

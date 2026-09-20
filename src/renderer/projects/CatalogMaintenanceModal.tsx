import { Alert, Button, Group, Modal, Stack, Text } from '@mantine/core';
import { useState } from 'react';

import type { CatalogMaintenancePreview } from '../../types/ForgeApi.js';
import { errorMessage } from '../../utils/Errors.ts';
import { formatBytes } from '../../utils/Format.ts';

interface CatalogMaintenanceModalProps {
  onChanged(): void;
}

export function CatalogMaintenanceModal({ onChanged }: CatalogMaintenanceModalProps) {
  const [opened, setOpened] = useState(false);
  const [busy, setBusy] = useState(false);
  const [preview, setPreview] = useState<CatalogMaintenancePreview>();
  const [error, setError] = useState<string>();

  async function open() {
    setOpened(true);
    setBusy(true);
    setError(undefined);
    try { setPreview(await window.forge.previewCatalogGarbageCollection()); }
    catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  }

  async function collect() {
    if (!preview) return;
    setBusy(true);
    setError(undefined);
    try {
      await window.forge.collectCatalogGarbage(preview.confirmationToken);
      setPreview(await window.forge.previewCatalogGarbageCollection());
      onChanged();
    } catch (cause) {
      setError(errorMessage(cause));
    } finally {
      setBusy(false);
    }
  }

  return <>
    <Button variant="default" onClick={() => void open()}>Catalog maintenance</Button>
    <Modal opened={opened} onClose={() => setOpened(false)} title="Asset catalog maintenance" size="lg">
      <Stack>
        {error && <Alert color="red" title="Catalog maintenance failed">{error}</Alert>}
        {preview && <>
          <Text size="sm">
            Scanned {preview.projectCount} projects and protected {preview.protectedAssetCount} of {preview.catalogAssetCount} catalog assets.
          </Text>
          <Text size="sm">
            {preview.candidateCount} unreferenced blobs use {formatBytes(preview.candidateBytes)};
            {' '}{preview.catalogCandidateCount} are catalog assets and will require re-import before reuse.
          </Text>
          {preview.candidateKinds.map((value) => <Text c="dimmed" size="xs" key={value}>{value}</Text>)}
          {preview.blockers.length > 0 && <Alert color="red" title="Cleanup blocked">
            {preview.blockers.map((value) => <Text size="xs" key={value}>{value}</Text>)}
          </Alert>}
        </>}
        <Group justify="flex-end">
          <Button variant="default" onClick={() => setOpened(false)}>Close</Button>
          <Button
            color="red"
            loading={busy}
            disabled={!preview?.candidateCount || Boolean(preview.blockers.length)}
            onClick={() => void collect()}
          >Delete unreferenced assets</Button>
        </Group>
      </Stack>
    </Modal>
  </>;
}

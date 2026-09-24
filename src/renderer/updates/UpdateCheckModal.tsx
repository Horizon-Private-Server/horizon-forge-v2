import { Alert, Center, Loader, Modal, Stack, Text } from '@mantine/core';
import { useEffect, useState } from 'react';

import type { UpdateCheckResult } from '../../types/Updates.js';
import { errorMessage } from '../../utils/Errors.ts';

interface UpdateCheckModalProps {
  opened: boolean;
  onClose(): void;
}

export function UpdateCheckModal({ opened, onClose }: UpdateCheckModalProps) {
  const [result, setResult] = useState<UpdateCheckResult>();

  useEffect(() => {
    if (!opened) return;
    let active = true;
    setResult(undefined);
    void window.forge.checkForUpdates()
      .then((value) => { if (active) setResult(value); })
      .catch((error) => {
        if (active) setResult({ status: 'error', title: 'Update check failed', message: errorMessage(error) });
      });
    return () => { active = false; };
  }, [opened]);

  return <Modal opened={opened} onClose={onClose} title="Check for updates" size="sm">
    {result
      ? <Alert color={resultColor(result.status)} title={result.title}>
        <Text size="sm" style={{ whiteSpace: 'pre-line' }}>{result.message}</Text>
      </Alert>
      : <Center py="lg"><Stack align="center" gap="xs"><Loader size="sm" /><Text size="sm">Checking for updates…</Text></Stack></Center>}
  </Modal>;
}

function resultColor(status: UpdateCheckResult['status']): string {
  if (status === 'error') return 'red';
  if (status === 'unavailable') return 'yellow';
  return status === 'current' ? 'teal' : 'cyan';
}

import { Alert, Modal, Stack, TextInput } from '@mantine/core';
import { useEffect, useState } from 'react';

import { errorMessage } from '../utils/Errors.ts';

interface SettingsModalProps {
  opened: boolean;
  onClose(): void;
}

export function SettingsModal({ opened, onClose }: SettingsModalProps) {
  const [sourceIso, setSourceIso] = useState('');
  const [error, setError] = useState<string>();

  useEffect(() => {
    if (!opened) return;
    void window.forge.getSettings().then((snapshot) => {
      const source = snapshot.entries.find((entry) => entry.key === 'sources.uya.iso');
      setSourceIso(typeof source?.value === 'string' ? source.value : '');
      setError(undefined);
    }).catch((reason: unknown) => setError(errorMessage(reason)));
  }, [opened]);

  return (
    <Modal opened={opened} onClose={onClose} title="Settings" size="lg">
      <Stack>
        <TextInput
          label="Clean UYA ISO"
          description="The verified source used by Forge. Change it from Forge → Setup."
          placeholder="Not configured"
          value={sourceIso}
          readOnly
        />
        {error && <Alert color="red" title="Settings error">{error}</Alert>}
      </Stack>
    </Modal>
  );
}

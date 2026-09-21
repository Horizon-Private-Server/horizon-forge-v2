import { Alert, Modal, Stack, Switch, TextInput } from '@mantine/core';
import { useEffect, useState } from 'react';

import { errorMessage } from '../../utils/Errors.ts';

interface SettingsModalProps {
  opened: boolean;
  onClose(): void;
  onViewportStatsChange(value: boolean): void;
}

export function SettingsModal({ opened, onClose, onViewportStatsChange }: SettingsModalProps) {
  const [sourceIso, setSourceIso] = useState('');
  const [showViewportStats, setShowViewportStats] = useState(true);
  const [error, setError] = useState<string>();

  useEffect(() => {
    if (!opened) return;
    void window.forge.getSettings().then((snapshot) => {
      const source = snapshot.entries.find((entry) => entry.key === 'sources.uya.iso');
      const stats = snapshot.entries.find((entry) => entry.key === 'ui.showViewportStats');
      setSourceIso(typeof source?.value === 'string' ? source.value : '');
      setShowViewportStats(stats?.value === true);
      onViewportStatsChange(stats?.value === true);
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
        <Switch
          checked={showViewportStats}
          label="Show viewport statistics"
          description="Display FPS, draw calls, and triangle count in the viewport."
          onChange={(event) => {
            const value = event.currentTarget.checked;
            setShowViewportStats(value);
            void window.forge.setSetting('ui.showViewportStats', value)
              .then(() => onViewportStatsChange(value))
              .catch((reason: unknown) => {
                setShowViewportStats(!value);
                setError(errorMessage(reason));
              });
          }}
        />
        {error && <Alert color="red" title="Settings error">{error}</Alert>}
      </Stack>
    </Modal>
  );
}

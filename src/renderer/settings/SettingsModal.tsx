import { Alert, Button, ColorInput, Modal, Select, SimpleGrid, Stack, Switch, Tabs, TextInput } from '@mantine/core';
import { useEffect, useState } from 'react';

import type { KeybindingMap, KeybindingOverrides } from '../../types/Keybindings.js';
import type { SceneTreeColors, SceneTreeKind } from '../../types/SceneTree.js';
import { errorMessage } from '../../utils/Errors.ts';
import { parseKeybindingOverrides, resolveKeybindings } from '../../utils/Keybindings.ts';
import {
  DEFAULT_SCENE_TREE_COLORS,
  isHexColor,
  readSceneTreeColors,
  SCENE_TREE_COLOR_KEYS,
  SCENE_TREE_KINDS,
  SCENE_TREE_LABELS,
} from '../../utils/SceneTreeColors.ts';
import { KeybindingsSettings } from './KeybindingsSettings.tsx';

interface SettingsModalProps {
  opened: boolean;
  onClose(): void;
  onKeybindingsChange(value: KeybindingMap): void;
  sceneTreeColors: SceneTreeColors;
  onSceneTreeColorsChange(value: SceneTreeColors): void;
  onViewportStatsChange(value: boolean): void;
}

export function SettingsModal({
  opened,
  onClose,
  onKeybindingsChange,
  sceneTreeColors,
  onSceneTreeColorsChange,
  onViewportStatsChange,
}: SettingsModalProps) {
  const [sourceIso, setSourceIso] = useState('');
  const [showViewportStats, setShowViewportStats] = useState(true);
  const [automaticUpdateChecks, setAutomaticUpdateChecks] = useState(true);
  const [updateChannel, setUpdateChannel] = useState<'stable' | 'nightly'>('stable');
  const [keybindingOverrides, setKeybindingOverrides] = useState<KeybindingOverrides>({});
  const [resettingTreeColors, setResettingTreeColors] = useState(false);
  const [error, setError] = useState<string>();

  useEffect(() => {
    if (!opened) return;
    void window.forge.getSettings().then((snapshot) => {
      const source = snapshot.entries.find((entry) => entry.key === 'sources.uya.iso');
      const stats = snapshot.entries.find((entry) => entry.key === 'ui.showViewportStats');
      const updates = snapshot.entries.find((entry) => entry.key === 'updates.automaticChecks');
      const channel = snapshot.entries.find((entry) => entry.key === 'updates.channel');
      const storedBindings = snapshot.entries.find((entry) => entry.key === 'keybindings.overrides')?.value;
      const overrides = parseKeybindingOverrides(storedBindings);
      setSourceIso(typeof source?.value === 'string' ? source.value : '');
      setShowViewportStats(stats?.value === true);
      setAutomaticUpdateChecks(updates?.value === true);
      setUpdateChannel(channel?.value === 'nightly' ? 'nightly' : 'stable');
      setKeybindingOverrides(overrides);
      onSceneTreeColorsChange(readSceneTreeColors(snapshot.entries));
      onKeybindingsChange(resolveKeybindings(overrides));
      onViewportStatsChange(stats?.value === true);
      setError(undefined);
    }).catch((reason: unknown) => setError(errorMessage(reason)));
  }, [opened]);

  const resetTreeColors = async () => {
    setResettingTreeColors(true);
    setError(undefined);
    try {
      for (const kind of SCENE_TREE_KINDS) await window.forge.resetSettings(SCENE_TREE_COLOR_KEYS[kind]);
      onSceneTreeColorsChange(DEFAULT_SCENE_TREE_COLORS);
    } catch (reason) {
      setError(errorMessage(reason));
    } finally {
      setResettingTreeColors(false);
    }
  };

  return (
    <Modal opened={opened} onClose={onClose} title="Settings" size="xl">
      <Tabs defaultValue="general">
        <Tabs.List>
          <Tabs.Tab value="general">General</Tabs.Tab>
          <Tabs.Tab value="customization">Customization</Tabs.Tab>
          <Tabs.Tab value="keybindings">Keybindings</Tabs.Tab>
        </Tabs.List>
        <Tabs.Panel value="general" pt="sm">
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
            <Select
              label="Update channel"
              description="Stable receives tagged releases; nightly receives successful main-branch builds."
              value={updateChannel}
              data={[
                { value: 'stable', label: 'Stable' },
                { value: 'nightly', label: 'Nightly' },
              ]}
              allowDeselect={false}
              onChange={(value) => {
                if (value !== 'stable' && value !== 'nightly') return;
                const previous = updateChannel;
                setUpdateChannel(value);
                void window.forge.setSetting('updates.channel', value).catch((reason: unknown) => {
                  setUpdateChannel(previous);
                  setError(errorMessage(reason));
                });
              }}
            />
            <Switch
              checked={automaticUpdateChecks}
              label="Automatically check for updates"
              description="Check the selected update channel and ask before downloading."
              onChange={(event) => {
                const value = event.currentTarget.checked;
                setAutomaticUpdateChecks(value);
                void window.forge.setSetting('updates.automaticChecks', value).catch((reason: unknown) => {
                  setAutomaticUpdateChecks(!value);
                  setError(errorMessage(reason));
                });
              }}
            />
            {error && <Alert color="red" title="Settings error">{error}</Alert>}
          </Stack>
        </Tabs.Panel>
        <Tabs.Panel value="customization" pt="sm">
          <SimpleGrid cols={{ base: 1, sm: 2 }}>
            {SCENE_TREE_KINDS.map((kind) => <ColorInput
              key={kind}
              label={`${SCENE_TREE_LABELS[kind]} tree color`}
              value={sceneTreeColors[kind]}
              format="hex"
              withEyeDropper={false}
              onChange={(value) => {
                if (isHexColor(value)) onSceneTreeColorsChange({ ...sceneTreeColors, [kind]: value });
              }}
              onChangeEnd={(value) => persistSceneTreeColor(kind, value, onSceneTreeColorsChange, setError)}
            />)}
          </SimpleGrid>
          <Button mt="sm" variant="default" loading={resettingTreeColors} onClick={() => void resetTreeColors()}>
            Reset tree colors
          </Button>
          {error && <Alert color="red" title="Settings error" mt="sm">{error}</Alert>}
        </Tabs.Panel>
        <Tabs.Panel value="keybindings" pt="sm">
          <KeybindingsSettings
            bindings={resolveKeybindings(keybindingOverrides)}
            overrides={keybindingOverrides}
            onChange={(overrides) => {
              setKeybindingOverrides(overrides);
              onKeybindingsChange(resolveKeybindings(overrides));
            }}
          />
        </Tabs.Panel>
      </Tabs>
    </Modal>
  );
}

function persistSceneTreeColor(
  kind: SceneTreeKind,
  value: string,
  onChange: (colors: SceneTreeColors) => void,
  onError: (error: string) => void,
): void {
  if (!isHexColor(value)) return;
  void window.forge.setSetting(SCENE_TREE_COLOR_KEYS[kind], value).catch((reason: unknown) => {
    onError(errorMessage(reason));
    void window.forge.getSettings().then((snapshot) => onChange(readSceneTreeColors(snapshot.entries)));
  });
}

import { Button, Paper, SimpleGrid, Text } from '@mantine/core';
import { useEffect, useState } from 'react';

import type { CollisionVisualization } from '../../types/CollisionVisualization.js';
import {
  cloneUyaCollisionVisualization,
  DEFAULT_UYA_COLLISION_VISUALIZATION,
  serializeUyaCollisionVisualization,
  UYA_COLLISION_VISUALIZATION_KEY,
} from '../../utils/UyaCollisionVisualization.ts';
import { formatUyaCollisionTypeId, formatUyaSoundTypeId } from '../../utils/CollisionFormat.ts';
import { errorMessage } from '../../utils/Errors.ts';
import { isHexColor } from '../../utils/SceneTreeColors.ts';
import { ColorPickerInput } from '../ColorPickerInput.tsx';

interface UyaCollisionVisualizationSettingsProps {
  value: CollisionVisualization;
  onChange(value: CollisionVisualization): void;
  onError(value?: string): void;
}

export function UyaCollisionVisualizationSettings({
  value,
  onChange,
  onError,
}: UyaCollisionVisualizationSettingsProps) {
  const [colors, setColors] = useState(() => cloneUyaCollisionVisualization(value));
  const [resetting, setResetting] = useState(false);
  useEffect(() => setColors(cloneUyaCollisionVisualization(value)), [value]);

  const persist = async (next: CollisionVisualization) => {
    setColors(next);
    onError(undefined);
    try {
      await window.forge.setSetting(UYA_COLLISION_VISUALIZATION_KEY, serializeUyaCollisionVisualization(next));
      onChange(next);
    } catch (reason) {
      setColors(cloneUyaCollisionVisualization(value));
      onError(errorMessage(reason));
    }
  };

  const reset = async () => {
    setResetting(true);
    onError(undefined);
    try {
      await window.forge.resetSettings(UYA_COLLISION_VISUALIZATION_KEY);
      const defaults = cloneUyaCollisionVisualization(DEFAULT_UYA_COLLISION_VISUALIZATION);
      setColors(defaults);
      onChange(defaults);
    } catch (reason) {
      onError(errorMessage(reason));
    } finally {
      setResetting(false);
    }
  };

  return <Paper withBorder p="md" mt="xl">
    <Text fw={500}>UYA collision visualization</Text>
    <Text c="dimmed" size="sm" mb="md">
      Preview-only colors. They do not change collision IDs, project dirtiness, or built maps.
    </Text>
    <Text fw={500} size="sm" mb="xs">Collision type colors</Text>
    <SimpleGrid cols={{ base: 1, sm: 2, md: 4 }}>
      {colors.collisionTypeColors.map((color, index) => <ColorPickerInput
        key={`collision-${index}`}
        label={formatUyaCollisionTypeId(index)}
        value={color}
        format="hex"
        onChange={(next) => {
          if (isHexColor(next)) setColors(replacePaletteColor(colors, 'collisionTypeColors', index, next));
        }}
        onChangeEnd={(next) => {
          if (isHexColor(next)) void persist(replacePaletteColor(colors, 'collisionTypeColors', index, next));
        }}
      />)}
    </SimpleGrid>
    <Text fw={500} size="sm" mt="md" mb="xs">Sound accent colors</Text>
    <SimpleGrid cols={{ base: 1, sm: 2, md: 4 }}>
      {colors.soundTypeColors.map((color, index) => <ColorPickerInput
        key={`sound-${index}`}
        label={formatUyaSoundTypeId(index)}
        value={color}
        format="hex"
        onChange={(next) => {
          if (isHexColor(next)) setColors(replacePaletteColor(colors, 'soundTypeColors', index, next));
        }}
        onChangeEnd={(next) => {
          if (isHexColor(next)) void persist(replacePaletteColor(colors, 'soundTypeColors', index, next));
        }}
      />)}
    </SimpleGrid>
    <SimpleGrid cols={{ base: 1, sm: 2 }} mt="md">
      <ColorPickerInput
        label="Player barrier color"
        value={colors.playerBarrierColor}
        format="hex"
        onChange={(next) => {
          if (isHexColor(next)) setColors({ ...colors, playerBarrierColor: next });
        }}
        onChangeEnd={(next) => {
          if (isHexColor(next)) void persist({ ...colors, playerBarrierColor: next });
        }}
      />
    </SimpleGrid>
    <Button mt="md" variant="default" loading={resetting} onClick={() => void reset()}>
      Reset UYA collision colors
    </Button>
  </Paper>;
}

function replacePaletteColor(
  value: CollisionVisualization,
  key: 'collisionTypeColors' | 'soundTypeColors',
  index: number,
  color: string,
): CollisionVisualization {
  const palette = [...value[key]];
  palette[index] = color;
  return { ...value, [key]: palette };
}

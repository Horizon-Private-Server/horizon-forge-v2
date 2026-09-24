import { Alert, Box, Button, Checkbox, Group, Progress, Stack, Text } from '@mantine/core';
import { useEffect, useMemo, useState } from 'react';

import type { EditorSnapshot } from '../../types/EditorRuntime.js';
import type { BuildLayerId, BuildLayerStatus, BuildPlan } from '../../types/ForgeApi.js';
import { errorMessage } from '../../utils/Errors.ts';
import { useEditor } from './EditorContext.ts';
import { EditorPanel } from './EditorPrimitives.tsx';

const ALL_LAYERS: BuildLayerId[] = [
  'World', 'Sky', 'Tfrags', 'Collision', 'Ties', 'Shrubs', 'Mobys', 'Gameplay', 'Lighting', 'Opaque',
];

export function BuildPanel() {
  const { project, busy, hostAvailable, buildProgress, buildResult, build, cancelBuild } = useEditor();
  const [plan, setPlan] = useState<BuildPlan>();
  const [planError, setPlanError] = useState<string>();
  const [selected, setSelected] = useState(() => new Set(ALL_LAYERS));
  const unsaved = useMemo(() => unsavedLayerStates(project), [project]);

  useEffect(() => {
    let disposed = false;
    setPlanError(undefined);
    void window.forge.getBuildPlan()
      .then((value) => { if (!disposed) setPlan(value); })
      .catch((cause) => { if (!disposed) setPlanError(errorMessage(cause)); });
    return () => { disposed = true; };
  }, [project.projectId, project.isDirty, buildResult]);

  useEffect(() => {
    setSelected(new Set(ALL_LAYERS));
  }, [project.projectId]);

  const layers = plan?.layers.map((layer) => ({
    ...layer,
    state: unsaved.get(layer.layer) ?? layer.state,
  })) ?? [];
  const includedLayers = new Set(selected);
  for (const layer of layers) {
    if (layer.state !== 'Clean' && !layer.canDefer) includedLayers.add(layer.layer);
  }

  const toggle = (layer: BuildLayerId, checked: boolean) => setSelected((current) => {
    const next = new Set(current);
    if (checked) next.add(layer);
    else next.delete(layer);
    return next;
  });

  return <EditorPanel label="Build">
    <Stack h="100%">
      <div>
        <Text fw={600}>Build development ISO</Text>
        <Text size="xs" c="dimmed">Choose which changed layers to rebuild. Unchecked layers keep their last staged output.</Text>
      </div>
      {planError && <Alert color="red" title="Build plan unavailable">{planError}</Alert>}
      {layers.map((layer) => {
        const required = layer.state !== 'Clean' && !layer.canDefer;
        return <Checkbox
          key={layer.layer}
          checked={selected.has(layer.layer) || required}
          disabled={busy || required}
          onChange={(event) => toggle(layer.layer, event.currentTarget.checked)}
          label={<Group gap="xs" wrap="nowrap" title={layerDescription(layer.layer)}>
            <Text size="sm">{layerLabel(layer.layer)}</Text>
            <Text size="xs" c={stateColor(layer.state)}>{stateLabel(layer)}</Text>
          </Group>}
        />;
      })}
      {!plan && !planError && <Text size="xs" c="dimmed">Loading build plan…</Text>}
      {buildProgress && busy && <div>
        <Group justify="space-between" wrap="nowrap">
          <Text size="xs">{buildProgress.message}</Text>
          <Text size="xs" c="dimmed">{buildProgress.phase}</Text>
        </Group>
        <Progress
          value={buildProgress.total ? buildProgress.completed / buildProgress.total * 100 : 0}
          size="xs"
        />
      </div>}
      {buildResult && <Alert color={buildResult.succeeded ? 'teal' : 'red'} title={buildResult.succeeded ? 'Build complete' : 'Build failed'}>
        {buildResult.message} {buildResult.nextAction}
      </Alert>}
      <Box mt="auto">
        {busy
          ? <Button fullWidth variant="default" onClick={() => void cancelBuild()}>Cancel</Button>
          : <Button fullWidth disabled={!hostAvailable || !plan}
            onClick={() => void build([...includedLayers])}>Build</Button>}
      </Box>
    </Stack>
  </EditorPanel>;
}

function unsavedLayerStates(project: EditorSnapshot): Map<BuildLayerId, BuildLayerStatus['state']> {
  const result = new Map<BuildLayerId, BuildLayerStatus['state']>();
  const byProjectLayer: Record<string, BuildLayerId> = { ties: 'Ties', shrubs: 'Shrubs', mobys: 'Mobys' };
  for (const entity of project.entities) {
    const layer = byProjectLayer[entity.layer.toLowerCase()];
    if (layer && entity.state.dirty) result.set(layer, 'Dirty');
  }
  if (result.has('Mobys')) result.set('Gameplay', 'DependencyInvalidated');
  if (['Ties', 'Shrubs', 'Mobys'].some((layer) => result.has(layer as BuildLayerId)))
    result.set('Lighting', 'DependencyInvalidated');
  return result;
}

function layerLabel(layer: BuildLayerId): string {
  return layer === 'Opaque' ? 'Preserved Data' : layer;
}

function layerDescription(layer: BuildLayerId): string | undefined {
  return layer === 'Opaque'
    ? 'Code, audio, HUD, and unsupported map sections copied unchanged.'
    : undefined;
}

function stateLabel(layer: BuildLayerStatus): string {
  if (layer.state === 'Clean') return 'Up to date';
  if (layer.state === 'Blocked') return 'Blocked';
  if (layer.state === 'DependencyInvalidated') return 'Needs rebuild (dependency changed)';
  return layer.canDefer ? 'Needs to be rebuilt' : 'Needs initial build';
}

function stateColor(state: BuildLayerStatus['state']): string {
  if (state === 'Clean') return 'dimmed';
  if (state === 'Blocked') return 'red';
  return 'yellow';
}

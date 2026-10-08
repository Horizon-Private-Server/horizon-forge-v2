import { Button, Checkbox, Fieldset, Group, Select, Stack, Text } from '@mantine/core';
import { useEffect, useRef, useState } from 'react';

import type { EditorEntity, EditorInstancedCollisionBinding } from '../../types/EditorRuntime.js';
import {
  collisionTypeId, collisionTypeIdOptions, defaultCollisionType, formatCollisionType,
  packCollisionType, soundTypeId, soundTypeIdOptions,
} from '../../utils/CollisionFormat.ts';
import { useEditor } from './EditorContext.ts';

interface InstancedCollisionPainterProps {
  entity: EditorEntity;
  binding: EditorInstancedCollisionBinding;
  disabled: boolean;
  instanceCount: number;
  showControls: boolean;
  visible: boolean;
  active: boolean;
  onActiveChange(active: boolean): void;
  wireframe: boolean;
  onWireframeChange(checked: boolean): Promise<void>;
}

export function InstancedCollisionPainter({
  entity,
  binding,
  disabled,
  instanceCount,
  showControls,
  visible,
  active,
  onActiveChange,
  wireframe,
  onWireframeChange,
}: InstancedCollisionPainterProps) {
  const {
    busy, execute, project, setInstancedCollisionOverlay, instancedCollisionOverlay,
  } = useEditor();
  const [rawType, setRawType] = useState(
    binding.recipe.rawType ?? defaultCollisionType(project.target.game),
  );
  const [interaction, setInteraction] = useState<'paint' | 'reset' | 'eyedropper'>('paint');
  const [hoveredFace, setHoveredFace] = useState<{ faceId: number; rawType: number }>();
  const [modelStatus, setModelStatus] = useState<'loading' | 'ready' | 'failed'>();
  const activeRef = useRef(active);
  activeRef.current = active;

  useEffect(() => {
    setRawType(binding.recipe.rawType ?? defaultCollisionType(project.target.game));
    setInteraction('paint');
    setHoveredFace(undefined);
  }, [entity.id]);

  useEffect(() => {
    if (!visible) {
      setModelStatus(undefined);
      setInstancedCollisionOverlay(undefined);
      return;
    }
    let disposed = false;
    const requestToken = crypto.randomUUID();
    setModelStatus('loading');
    void window.forge.getAppliedInstancedCollisionModel(binding.proxyAssetId, requestToken).then((source) => {
      if (disposed) return;
      setInstancedCollisionOverlay({
        entityId: entity.id,
        url: source.url,
        showSource: true,
        showProxy: true,
        wireframe,
        showOctants: false,
        paint: {
          active: activeRef.current,
          proxyAssetId: binding.proxyAssetId,
          defaultRawType: binding.recipe.rawType,
          faceTypes: binding.faceTypeOverrides,
          brushRawType: rawType,
          interaction,
          onHover: (faceId, faceRawType) => setHoveredFace((current) => {
            if (faceId === undefined || faceRawType === undefined) return undefined;
            return current?.faceId === faceId && current.rawType === faceRawType
              ? current
              : { faceId, rawType: faceRawType };
          }),
          onStroke: (faceIds, strokeRawType) => execute({
            id: crypto.randomUUID(),
            kind: 'setInstancedCollisionFaceTypes',
            entityIds: [entity.id],
            expectedProxyAssetId: binding.proxyAssetId,
            faceTypes: faceIds.map((faceIndex) => ({ faceIndex, rawType: strokeRawType })),
          }),
          onEyedropper: (faceRawType) => {
            setRawType(faceRawType);
            setInteraction('paint');
          },
        },
      });
      setModelStatus('ready');
    }).catch(() => {
      if (!disposed) setModelStatus('failed');
    });
    return () => {
      disposed = true;
      void window.forge.cancelAssetPreview(requestToken);
      setInstancedCollisionOverlay(undefined);
    };
  }, [binding.proxyAssetId, entity.id, execute, setInstancedCollisionOverlay, visible]);

  useEffect(() => {
    if (!instancedCollisionOverlay?.paint
      || instancedCollisionOverlay.paint.proxyAssetId !== binding.proxyAssetId) return;
    setInstancedCollisionOverlay({
      ...instancedCollisionOverlay,
      wireframe,
      paint: {
        ...instancedCollisionOverlay.paint,
        active,
        defaultRawType: binding.recipe.rawType,
        faceTypes: binding.faceTypeOverrides,
        brushRawType: rawType,
        interaction,
      },
    });
  }, [active, binding.faceTypeOverrides, binding.recipe.rawType,
    interaction, rawType, instancedCollisionOverlay?.paint?.proxyAssetId, wireframe]);

  if (!showControls) return null;
  return <>
    <Button size="xs" variant={active ? 'filled' : 'light'} disabled={disabled || busy}
      onClick={() => {
        onActiveChange(!active);
        setHoveredFace(undefined);
        setInteraction('paint');
      }}>
      {active ? 'Done painting' : 'Paint collision types'}
    </Button>
    {active && <Fieldset legend="Collision type painter">
      <Stack gap="xs">
        <Text size="xs">
          Painting the exact proxy faces used by {instanceCount.toLocaleString()} {instanceCount === 1 ? 'instance' : 'matching instances'}.
          Middle-drag to orbit. Alt-drag also works. Escape cancels the active stroke.
        </Text>
        <Group grow align="end">
          <Select label="Collision ID"
            value={String(collisionTypeId(rawType, project.target.game))}
            data={collisionTypeIdOptions(project.target.game)} allowDeselect={false}
            onChange={(value) => value !== null && setRawType(packCollisionType(
              Number(value), soundTypeId(rawType, project.target.game), project.target.game,
            ))} />
          <Select label="Sound ID"
            value={String(soundTypeId(rawType, project.target.game))}
            data={soundTypeIdOptions(project.target.game)} allowDeselect={false}
            onChange={(value) => value !== null && setRawType(packCollisionType(
              collisionTypeId(rawType, project.target.game), Number(value), project.target.game,
            ))} />
        </Group>
        <Group gap="xs">
          <Button size="compact-xs" variant={interaction === 'paint' ? 'filled' : 'light'}
            onClick={() => setInteraction('paint')}>Paint</Button>
          <Button size="compact-xs" variant={interaction === 'reset' ? 'filled' : 'light'}
            onClick={() => setInteraction('reset')}>Reset face</Button>
          <Button size="compact-xs" variant={interaction === 'eyedropper' ? 'filled' : 'light'}
            onClick={() => setInteraction('eyedropper')}>Eyedropper</Button>
          <Button size="compact-xs" variant="subtle" disabled={binding.faceTypeOverrides.length === 0}
            onClick={() => void execute({
              id: crypto.randomUUID(),
              kind: 'setInstancedCollisionFaceTypes',
              entityIds: [entity.id],
              expectedProxyAssetId: binding.proxyAssetId,
              faceTypes: binding.faceTypeOverrides.map((value) => ({
                faceIndex: value.faceIndex,
                rawType: binding.recipe.rawType,
              })),
            })}>Reset all</Button>
        </Group>
        <Group>
          <Checkbox label="Source" checked={instancedCollisionOverlay?.showSource ?? true}
            onChange={(event) => instancedCollisionOverlay && setInstancedCollisionOverlay({
              ...instancedCollisionOverlay, showSource: event.currentTarget.checked,
            })} />
          <Checkbox label="Proxy" checked={instancedCollisionOverlay?.showProxy ?? true}
            onChange={(event) => instancedCollisionOverlay && setInstancedCollisionOverlay({
              ...instancedCollisionOverlay, showProxy: event.currentTarget.checked,
            })} />
          <Checkbox label="Wireframe" checked={wireframe}
            onChange={(event) => void onWireframeChange(event.currentTarget.checked)} />
        </Group>
        <Text size="xs" c="dimmed" aria-live="polite">
          {hoveredFace
            ? `Face ${hoveredFace.faceId.toLocaleString()} · ${formatCollisionType(hoveredFace.rawType, project.target.game)}`
            : 'Point at a proxy face to inspect its IDs.'}
        </Text>
        {modelStatus === 'loading' && <Text size="xs" c="dimmed">Loading applied proxy…</Text>}
        {modelStatus === 'failed' && <Text size="xs" c="red">
          Applied proxy could not be loaded. Restore or regenerate it before painting.
        </Text>}
      </Stack>
    </Fieldset>}
  </>;
}

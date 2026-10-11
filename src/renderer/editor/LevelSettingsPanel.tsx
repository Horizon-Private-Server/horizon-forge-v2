import {
  Alert, Badge, Checkbox, Code, Fieldset, Group, NumberInput, Slider, Stack, Text,
} from '@mantine/core';
import { useEffect, useMemo, useRef, useState } from 'react';

import type {
  EditorLevelReference, EditorLevelSceneSettings, EditorLevelSettings, ProjectVector3,
} from '../../types/EditorRuntime.js';
import { parseRgb } from '../../utils/Color.ts';
import {
  levelReferenceCandidates, levelReferenceState, levelReferenceValue,
} from '../../utils/LevelSettings.ts';
import { ColorPickerInput } from '../ColorPickerInput.tsx';
import { useEditor } from './EditorContext.ts';
import { EditorEmptyState, EditorPanel, EditorProperty, EditorPropertyGrid } from './EditorPrimitives.tsx';
import { ReferenceField } from './ReferenceField.tsx';

type SceneReferenceKey = 'shipPath' | 'shipCameraCuboidStart' | 'shipCameraCuboidEnd';

export function LevelSettingsPanel() {
  const {
    project, terrain, execute, busy, showOcclusionOctants, setShowOcclusionOctants, navigateToEntity,
  } = useEditor();
  const editable = project.levelSettings;
  const settings = editable ? { ...terrain?.environment, ...editable } : terrain?.environment;
  const scene = editable?.scene;
  const splines = useMemo(() => levelReferenceCandidates(
    project.entities, 'spline', 'gameplay/core/splines',
  ), [project.entities]);
  const cuboids = useMemo(() => levelReferenceCandidates(
    project.entities, 'cuboid', 'gameplay/core/cuboids',
  ), [project.entities]);
  const update = (value: Partial<EditorLevelSettings>) => editable
    ? execute({
      id: crypto.randomUUID(), kind: 'updateLevelSettings', entityIds: [],
      levelSettings: { ...editable, ...value },
    })
    : Promise.resolve(false);
  const updateScene = <K extends keyof EditorLevelSceneSettings>(key: K, value: EditorLevelSceneSettings[K]) =>
    scene ? update({ scene: { ...scene, [key]: value } }) : Promise.resolve(false);
  const updateReference = (
    key: SceneReferenceKey,
    entityId: string | undefined,
    candidates: ReturnType<typeof levelReferenceCandidates>,
  ) => {
    const value = levelReferenceValue(entityId, candidates);
    if (value) void updateScene(key, value);
  };
  const updatePaletteOptimization = (strength: number) => void execute({
    id: crypto.randomUUID(), kind: 'updatePaletteOptimization', entityIds: [],
    paletteOptimization: { mappingVersion: 'paletteOptimization.v1', strength },
  });
  const paletteControl = <Stack gap={2}>
    <LevelSlider label="Texture palette optimization" value={project.target.paletteOptimization.strength}
      max={100} disabled={busy} onCommit={updatePaletteOptimization} />
    <Group justify="space-between">
      <Text size="xs" c="dimmed">Visual fidelity</Text>
      <Text size="xs" c="dimmed">VRAM savings</Text>
    </Group>
  </Stack>;
  if (!settings) return <EditorPanel label="Level settings">
    <Stack>{paletteControl}<EditorEmptyState message="Level settings are unavailable." /></Stack>
  </EditorPanel>;
  const rgb = (value: readonly number[]) => `rgb(${value.join(', ')})`;
  const fogDistanceMax = Math.max(1000, settings.fogNearDistance, settings.fogFarDistance);
  return <EditorPanel label="Level settings">
    <Stack>
      {paletteControl}
      <Checkbox
        checked={showOcclusionOctants}
        label={`Show occlusion octants (${(terrain?.occlusionOctants.length ?? 0).toLocaleString()})`}
        onChange={(event) => setShowOcclusionOctants(event.currentTarget.checked)}
      />
      <Fieldset legend="Appearance">
        <Stack gap="xs">
          {!editable && <Text size="xs" c="yellow">Migrate this project to edit imported level settings.</Text>}
          <LevelColorInput label="Background color" value={rgb(settings.backgroundColor)} disabled={!editable || busy}
            onCommit={(value) => { void update({ backgroundColor: value }); }} />
          <LevelColorInput label="Fog color" value={rgb(settings.fogColor)} disabled={!editable || busy}
            onCommit={(value) => { void update({ fogColor: value }); }} />
          <LevelSlider label="Fog near distance" value={settings.fogNearDistance} max={fogDistanceMax}
            precision={2} disabled={!editable || busy}
            onCommit={(value) => { void update({ fogNearDistance: value }); }} />
          <LevelSlider label="Fog far distance" value={settings.fogFarDistance} max={fogDistanceMax}
            precision={2} disabled={!editable || busy}
            onCommit={(value) => { void update({ fogFarDistance: value }); }} />
          <LevelSlider label="Fog near intensity" value={settings.fogNearIntensity} max={255}
            disabled={!editable || busy} onCommit={(value) => { void update({ fogNearIntensity: value }); }} />
          <LevelSlider label="Fog far intensity" value={settings.fogFarIntensity} max={255}
            disabled={!editable || busy} onCommit={(value) => { void update({ fogFarIntensity: value }); }} />
        </Stack>
      </Fieldset>
      <Group justify="space-between" align="center">
        <Text fw={600} size="sm">Scene</Text>
        <Badge color={scene ? 'green' : 'gray'} variant="light">{scene ? 'UYA editable' : 'Unavailable'}</Badge>
      </Group>
      {!scene && <Alert color="yellow" title="Advanced settings unavailable">
        {project.target.game === 'UYA'
          ? 'This project has no verified UYA scene-settings payload. Re-import or repair it from its source level.'
          : `${project.target.game} does not expose editable advanced scene settings yet.`}
      </Alert>}
      {scene && <>
        <Fieldset legend="World">
          <Stack gap="xs">
            <LevelNumberInput label="Death height" unit="world units" value={scene.deathHeight}
              disabled={busy} onCommit={(value) => updateScene('deathHeight', value)} />
            <Checkbox
              aria-label="Use spherical world"
              checked={scene.isSphericalWorld}
              disabled={busy}
              label="Spherical world"
              description="Use the sphere center for world-space behavior."
              onChange={(event) => { void updateScene('isSphericalWorld', event.currentTarget.checked); }}
            />
            <LevelVectorInput label="Sphere center" unit="world units" value={scene.sphereCenter}
              disabled={busy} onCommit={(value) => updateScene('sphereCenter', value)} />
          </Stack>
        </Fieldset>
        <Fieldset legend="Ship">
          <Stack gap="xs">
            <LevelVectorInput label="Ship position" unit="world units" value={scene.shipPosition}
              disabled={busy} onCommit={(value) => updateScene('shipPosition', value)} />
            <LevelNumberInput label="Ship rotation Z" unit="radians" value={scene.shipRotationZ}
              disabled={busy} onCommit={(value) => updateScene('shipRotationZ', value)} />
            <LevelReferenceInput label="Ship path" fieldKey="level.shipPath" targetKind="spline"
              value={scene.shipPath} candidates={splines} entities={project.entities} disabled={busy}
              ownerEntityId={project.projectId}
              onChange={(entityId) => updateReference('shipPath', entityId, splines)}
              onNavigate={navigateToEntity} />
            <LevelReferenceInput label="Camera start cuboid" fieldKey="level.shipCameraCuboidStart"
              targetKind="cuboid" value={scene.shipCameraCuboidStart} candidates={cuboids}
              entities={project.entities} disabled={busy} ownerEntityId={project.projectId}
              onChange={(entityId) => updateReference('shipCameraCuboidStart', entityId, cuboids)}
              onNavigate={navigateToEntity} />
            <LevelReferenceInput label="Camera end cuboid" fieldKey="level.shipCameraCuboidEnd"
              targetKind="cuboid" value={scene.shipCameraCuboidEnd} candidates={cuboids}
              entities={project.entities} disabled={busy} ownerEntityId={project.projectId}
              onChange={(entityId) => updateReference('shipCameraCuboidEnd', entityId, cuboids)}
              onNavigate={navigateToEntity} />
          </Stack>
        </Fieldset>
      </>}
      <Fieldset legend="Source">
        <EditorPropertyGrid>
          <EditorProperty label="Target"><Code>{project.target.game} {project.target.revision}</Code></EditorProperty>
          <EditorProperty label="Chunk planes">{settings.chunkPlaneCount ?? 0}</EditorProperty>
          <EditorProperty label="Core sounds">{settings.coreSoundsCount ?? 0}</EditorProperty>
        </EditorPropertyGrid>
      </Fieldset>
    </Stack>
  </EditorPanel>;
}

function LevelReferenceInput({
  label, ownerEntityId, fieldKey, targetKind, value, candidates, entities, disabled, onChange, onNavigate,
}: {
  label: string;
  ownerEntityId: string;
  fieldKey: string;
  targetKind: 'spline' | 'cuboid';
  value: EditorLevelReference;
  candidates: ReturnType<typeof levelReferenceCandidates>;
  entities: ReturnType<typeof levelReferenceCandidates>;
  disabled: boolean;
  onChange(entityId?: string): void;
  onNavigate(entityId: string, action: 'select' | 'reveal' | 'focus'): void;
}) {
  const state = levelReferenceState(ownerEntityId, fieldKey, targetKind, value, candidates, entities);
  return <Stack gap={2}>
    <Text size="xs" fw={500}>{label}</Text>
    <ReferenceField reference={state.reference} candidates={candidates} disabled={disabled} ariaLabel={label}
      showFieldKey={false} onChange={onChange} onNavigate={onNavigate} />
    {state.diagnostic
      ? <Text role="alert" c="red" size="xs">{state.diagnostic}</Text>
      : <Text c="dimmed" size="xs">Drop a source-backed {targetKind} from the Scene Tree; double-click to focus.</Text>}
  </Stack>;
}

function LevelNumberInput({ label, unit, value, disabled, onCommit }: {
  label: string;
  unit: string;
  value: number;
  disabled: boolean;
  onCommit(value: number): Promise<boolean>;
}) {
  const [draft, setDraft] = useState<number | string>(value);
  const [rejected, setRejected] = useState(false);
  const cancelled = useRef(false);
  useEffect(() => {
    setDraft(value);
    setRejected(false);
  }, [value]);
  const valid = typeof draft === 'number' && Number.isFinite(draft);
  const commit = async () => {
    if (cancelled.current) {
      cancelled.current = false;
      setDraft(value);
      setRejected(false);
    } else if (valid && draft !== value) {
      const applied = await onCommit(draft);
      setRejected(!applied);
      if (!applied) setDraft(value);
    }
  };
  const error = !valid ? 'Enter a finite number.' : rejected ? 'The change was rejected.' : undefined;
  return <NumberInput
    aria-label={`${label} in ${unit}`}
    label={label}
    description={unit}
    decimalScale={4}
    disabled={disabled}
    error={error}
    value={draft}
    onChange={(next) => {
      setRejected(false);
      setDraft(next);
    }}
    onBlur={() => { void commit(); }}
    onKeyDown={(event) => {
      if (event.key === 'Enter') event.currentTarget.blur();
      if (event.key === 'Escape') {
        event.preventDefault();
        cancelled.current = true;
        event.currentTarget.blur();
      }
    }}
  />;
}

function LevelVectorInput({ label, unit, value, disabled, onCommit }: {
  label: string;
  unit: string;
  value: ProjectVector3;
  disabled: boolean;
  onCommit(value: ProjectVector3): Promise<boolean>;
}) {
  const [draft, setDraft] = useState<Record<keyof ProjectVector3, number | string>>({ ...value });
  const [rejected, setRejected] = useState(false);
  const cancelled = useRef(false);
  useEffect(() => {
    setDraft({ ...value });
    setRejected(false);
  }, [value.x, value.y, value.z]);
  const valid = Object.values(draft).every((item) => typeof item === 'number' && Number.isFinite(item));
  const commit = async () => {
    if (cancelled.current) {
      cancelled.current = false;
      setDraft({ ...value });
      setRejected(false);
    } else if (valid) {
      const next = draft as ProjectVector3;
      if (next.x === value.x && next.y === value.y && next.z === value.z) return;
      const applied = await onCommit(next);
      setRejected(!applied);
      if (!applied) setDraft({ ...value });
    }
  };
  return <Stack gap={2}>
    <Text size="xs" fw={500}>{label} ({unit})</Text>
    <Group grow wrap="nowrap">
      {(['x', 'y', 'z'] as const).map((axis) => <NumberInput
        key={axis}
        aria-label={`${label} ${axis.toUpperCase()} in ${unit}`}
        label={axis.toUpperCase()}
        decimalScale={4}
        disabled={disabled}
        value={draft[axis]}
        onChange={(next) => {
          setRejected(false);
          setDraft((current) => ({ ...current, [axis]: next }));
        }}
        onBlur={() => { void commit(); }}
        onKeyDown={(event) => {
          if (event.key === 'Enter') event.currentTarget.blur();
          if (event.key === 'Escape') {
            event.preventDefault();
            cancelled.current = true;
            event.currentTarget.blur();
          }
        }}
      />)}
    </Group>
    {!valid && <Text role="alert" c="red" size="xs">All coordinates must be finite numbers.</Text>}
    {rejected && <Text role="alert" c="red" size="xs">The vector change was rejected.</Text>}
  </Stack>;
}

function LevelColorInput({ label, value, disabled, onCommit }: {
  label: string;
  value: string;
  disabled: boolean;
  onCommit(value: [number, number, number]): void;
}) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  return <ColorPickerInput
    label={label} value={draft} format="rgb" disabled={disabled}
    onChange={setDraft} onChangeEnd={(next) => onCommit(parseRgb(next))}
  />;
}

function LevelSlider({ label, value, max, precision = 0, disabled, onCommit }: {
  label: string;
  value: number;
  max: number;
  precision?: number;
  disabled: boolean;
  onCommit(value: number): void;
}) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  return <Stack gap={2}>
    <Group justify="space-between"><Text size="xs" fw={500}>{label}</Text><Code>{draft.toFixed(precision)}</Code></Group>
    <Slider disabled={disabled} min={0} max={max} step={precision ? 0.01 : 1} value={draft}
      thumbLabel={label} onChange={setDraft} onChangeEnd={onCommit} />
  </Stack>;
}

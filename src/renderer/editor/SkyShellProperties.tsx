import { Button, Code, Fieldset, Group, NumberInput, Stack, Text } from '@mantine/core';
import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Mesh } from 'three';
import type { Object3D } from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';

import type { EditorEntity, ProjectVector3 } from '../../types/EditorRuntime.js';
import { errorMessage } from '../../utils/Errors.ts';
import { disposeObject } from '../../utils/Scene.ts';
import { useEditor } from './EditorContext.ts';
import { EditorProperty, EditorPropertyGrid } from './EditorPrimitives.tsx';
import { EntityStateControls, EntityTextEditor } from './EntityPropertyControls.tsx';

interface SkyShellSourceDetails {
  flags: number;
  blendMode: string;
  clusterCount: number;
  triangleCount: number;
  textureCount: number;
  initialRotationRadians: ProjectVector3;
  angularVelocityRadiansPerSecond: ProjectVector3;
}

export function SkyShellProperties({ entity }: { entity: EditorEntity }) {
  const { project, execute, busy } = useEditor();
  const shell = entity.skyShell!;
  const disabled = busy || entity.state.locked || entity.state.readOnly;
  const ordered = project.entities.filter((value) => value.skyShell)
    .sort((left, right) => left.skyShell!.order - right.skyShell!.order);
  const [details, setDetails] = useState<SkyShellSourceDetails>();
  const [detailsError, setDetailsError] = useState('');
  useEffect(() => {
    if (!entity.asset) return;
    const token = crypto.randomUUID();
    let disposed = false;
    setDetails(undefined);
    setDetailsError('');
    void window.forge.getAssetPreview(entity.asset.id, 'sky', token, shell.sourceShellIndex)
      .then((source) => new GLTFLoader().loadAsync(source.url))
      .then((gltf) => {
        const value = readSkyShellDetails(gltf.scene);
        disposeObject(gltf.scene);
        if (!disposed) setDetails(value);
      })
      .catch((cause: unknown) => { if (!disposed) setDetailsError(errorMessage(cause)); });
    return () => {
      disposed = true;
      void window.forge.cancelAssetPreview(token);
    };
  }, [entity.asset?.id, shell.sourceShellIndex]);
  const update = (value: {
    initialRotationRadians?: ProjectVector3;
    angularVelocityRadiansPerSecond?: ProjectVector3;
  }) => execute({ id: crypto.randomUUID(), kind: 'updateSkyShell', entityIds: [entity.id], update: value });
  const state = ['dirty', 'hidden', 'disabled', 'locked', 'invalid', 'missingAsset']
    .filter((key) => entity.state[key as keyof typeof entity.state]).join(', ') || 'Ready';
  const sourcePending = detailsError ? 'Unavailable' : 'Loading…';
  return <Stack gap="sm">
    <Fieldset legend="Sky shell">
      <Stack gap="xs">
        <EntityTextEditor identity={entity.id} label="Name" value={entity.name} disabled={disabled}
          onApply={(text) => execute({ id: crypto.randomUUID(), kind: 'renameEntity', entityIds: [entity.id], text })} />
        <EntityStateControls entities={[entity]} disabled={busy || entity.state.readOnly} />
        <EditorPropertyGrid>
          <EditorProperty label="Entity ID"><Code>{entity.id}</Code></EditorProperty>
          <EditorProperty label="Order">{shell.order + 1} of {ordered.length}</EditorProperty>
          <EditorProperty label="State">{state}</EditorProperty>
          <EditorProperty label="Compatibility">
            {entity.state.missingAsset ? 'Source asset missing' : entity.state.invalid ? 'Invalid' : 'Compatible'}
          </EditorProperty>
        </EditorPropertyGrid>
      </Stack>
    </Fieldset>
    <Fieldset legend="Rotation">
      <Stack gap="xs">
        <SkyVectorEditor label="Initial rotation" unit="degrees"
          value={radiansToDegrees(shell.initialRotationRadians)} disabled={disabled}
          onCommit={(value) => update({ initialRotationRadians: degreesToRadians(value) })} />
        <SkyVectorEditor label="Rotation speed" unit="degrees / second"
          value={radiansToDegrees(shell.angularVelocityRadiansPerSecond)} disabled={disabled}
          onCommit={(value) => update({ angularVelocityRadiansPerSecond: degreesToRadians(value) })} />
        <EditorPropertyGrid>
          <EditorProperty label="Speed magnitude">
            {vectorMagnitude(radiansToDegrees(shell.angularVelocityRadiansPerSecond)).toFixed(3)}° / second
          </EditorProperty>
        </EditorPropertyGrid>
        <Group grow>
          <Button variant="default" disabled={disabled || vectorMagnitude(shell.angularVelocityRadiansPerSecond) === 0}
            onClick={() => void update({ angularVelocityRadiansPerSecond: { x: 0, y: 0, z: 0 } })}>
            Stop rotation
          </Button>
          <Button variant="default" disabled={disabled || !details} onClick={() => details && void update({
            initialRotationRadians: details.initialRotationRadians,
            angularVelocityRadiansPerSecond: details.angularVelocityRadiansPerSecond,
          })}>Reset to source</Button>
        </Group>
      </Stack>
    </Fieldset>
    <Fieldset legend="Source">
      <EditorPropertyGrid>
        <EditorProperty label="Parent asset">{entity.asset ? <Code>{entity.asset.id}</Code> : 'Missing'}</EditorProperty>
        <EditorProperty label="Source">{entity.provenance
          ? `${entity.provenance.game} ${project.target.revision} level ${entity.provenance.level}`
          : 'Project-created'}</EditorProperty>
        <EditorProperty label="Source shell">{shell.sourceShellIndex}</EditorProperty>
        <EditorProperty label="Flags">{details
          ? `0x${(details.flags & 0xffff).toString(16).padStart(4, '0')}` : sourcePending}</EditorProperty>
        <EditorProperty label="Blend">{details?.blendMode ?? sourcePending}</EditorProperty>
        <EditorProperty label="Clusters">{details?.clusterCount ?? sourcePending}</EditorProperty>
        <EditorProperty label="Triangles">{details?.triangleCount ?? sourcePending}</EditorProperty>
        <EditorProperty label="Referenced textures">{details?.textureCount ?? sourcePending}</EditorProperty>
      </EditorPropertyGrid>
      {detailsError && <Text c="yellow" size="xs">Source statistics unavailable: {detailsError}</Text>}
    </Fieldset>
    <Button color="red" variant="light" disabled={disabled} onClick={() => void execute({
      id: crypto.randomUUID(), kind: 'deleteEntities', entityIds: [entity.id],
    })}>Remove sky shell</Button>
  </Stack>;
}

function SkyVectorEditor({ label, unit, value, disabled, onCommit }: {
  label: string;
  unit: string;
  value: ProjectVector3;
  disabled: boolean;
  onCommit(value: ProjectVector3): Promise<unknown>;
}) {
  const [draft, setDraft] = useState(value);
  const cancelled = useRef(false);
  const source = JSON.stringify(value);
  useLayoutEffect(() => setDraft(value), [source]);
  const valid = Object.values(draft).every(Number.isFinite);
  const commit = () => {
    if (cancelled.current) {
      cancelled.current = false;
      setDraft(value);
    } else if (valid && JSON.stringify(draft) !== source) void onCommit(draft);
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
        value={Number.isFinite(draft[axis]) ? draft[axis] : ''}
        onChange={(next) => setDraft({ ...draft, [axis]: typeof next === 'number' ? next : Number.NaN })}
        onBlur={commit}
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
    {!valid && <Text c="red" size="xs">Values must be finite.</Text>}
  </Stack>;
}

function readSkyShellDetails(root: Object3D): SkyShellSourceDetails {
  const values: Record<string, unknown>[] = [];
  root.traverse((object) => {
    values.push(object.userData);
    if (object instanceof Mesh) values.push(object.geometry.userData);
  });
  const first = (key: string) => values.find((value) => value[key] !== undefined)?.[key];
  const flags = Number(first('SkyboxShellFlags')) || 0;
  const textureIds = new Set(values.flatMap((value) => Array.isArray(value.TextureIds) ? value.TextureIds : [])
    .filter((value) => value !== 'untextured'));
  return {
    flags,
    blendMode: String(first('SkyboxDrawBlendMode') ?? ((flags & 0x2) !== 0 ? 'Bloom' : 'Source over')),
    clusterCount: Number(first('ClusterCount')) || 0,
    triangleCount: Number(first('TriangleCount')) || 0,
    textureCount: textureIds.size,
    initialRotationRadians: metadataVector(first('SkyboxShellRotationRadians')),
    angularVelocityRadiansPerSecond: metadataVector(first('SkyboxShellSourceAngularVelocityRadiansPerSecond')),
  };
}

function metadataVector(value: unknown): ProjectVector3 {
  if (!Array.isArray(value) || value.length < 3) return { x: 0, y: 0, z: 0 };
  return { x: Number(value[0]) || 0, y: Number(value[1]) || 0, z: Number(value[2]) || 0 };
}

function radiansToDegrees(value: ProjectVector3): ProjectVector3 {
  const scale = 180 / Math.PI;
  return { x: value.x * scale, y: value.y * scale, z: value.z * scale };
}

function degreesToRadians(value: ProjectVector3): ProjectVector3 {
  const scale = Math.PI / 180;
  return { x: value.x * scale, y: value.y * scale, z: value.z * scale };
}

function vectorMagnitude(value: ProjectVector3): number {
  return Math.hypot(value.x, value.y, value.z);
}

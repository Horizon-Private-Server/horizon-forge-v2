import { Accordion, Button, Checkbox, Code, Fieldset, Group, NumberInput, Select, Stack, Text } from '@mantine/core';
import { useEffect, useLayoutEffect, useRef, useState } from 'react';

import type {
  EditorCollisionOctantCost, EditorEntity, EditorInstancedCollisionCandidate, EditorInstancedCollisionPreview,
} from '../../types/EditorRuntime.js';
import {
  collisionTypeId, collisionTypeIdOptions, defaultCollisionType, formatCollisionType,
  packCollisionType, soundTypeId, soundTypeIdOptions,
} from '../../utils/CollisionFormat.ts';
import {
  recommendInstancedCollisionCandidate, instancedCollisionWorstOctantBytes,
} from '../../utils/InstancedCollision.ts';
import { useEditor } from './EditorContext.ts';
import { EditorProperty, EditorPropertyGrid } from './EditorPrimitives.tsx';
import { InstancedCollisionPainter } from './InstancedCollisionPainter.tsx';

const INSTANCED_COLLISION_METHOD_OPTIONS = [
  { value: 'surface-auto', label: 'Decimate (Automatic LOD)' },
  { value: 'surface-0', label: 'Decimate High LOD' },
  { value: 'surface-1', label: 'Decimate Med LOD' },
  { value: 'surface-2', label: 'Decimate Low LOD' },
  { value: 'hull', label: 'Shrinkwrap' },
];
const INSTANCED_COLLISION_MODE_OPTIONS = [
  { value: 'individual', label: 'Individual' },
  { value: 'shared', label: 'Shared' },
];
const DEFAULT_MAXIMUM_DEVIATION = 4;
type InstancedCollisionMethod = 'surface-auto' | 'surface-0' | 'surface-1' | 'surface-2' | 'hull';
const sourceSurfaceLodCache = new Map<string, number[]>();

export function InstancedCollisionProperties({ entity, disabled }: { entity: EditorEntity; disabled: boolean }) {
  const {
    applyInstancedCollisionPreview, busy, cancelInstancedCollisionPreview, execute, inspectInstancedCollisionSource,
    previewInstancedCollision, project,
    renderedInstancedCollisionEntityIds, setInstancedCollisionRendered,
    setInstancedCollisionOverlay, instancedCollisionOverlay,
  } = useEditor();
  const collision = entity.instancedCollisionEnabled === false
    ? entity.individualInstancedCollision
    : entity.instancedCollisionEnabled === true
      ? entity.instancedCollision
      : undefined;
  const [preview, setPreview] = useState<EditorInstancedCollisionPreview>();
  const [selectedToken, setSelectedToken] = useState('');
  const [generating, setGenerating] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState<string | null>(null);
  const [modelStatus, setModelStatus] = useState<'loading' | 'ready' | 'failed'>();
  const [rawType, setRawType] = useState(
    collision?.recipe.rawType ?? defaultCollisionType(project.target.game),
  );
  const [profileSections, setProfileSections] = useState(
    collision?.recipe.kind === 'hull' && collision.recipe.profileSections > 0
      ? collision.recipe.profileSections : 6,
  );
  const [method, setMethod] = useState<InstancedCollisionMethod>(instancedCollisionMethod(collision));
  const generation = useRef(0);
  const sourceInspection = useRef(0);
  const regenerationTimer = useRef<number | undefined>(undefined);
  const [wireframe, setWireframe] = useState(false);
  const renderCollision = renderedInstancedCollisionEntityIds.has(entity.id);
  const [painting, setPainting] = useState(false);
  const [debugInfo, setDebugInfo] = useState(false);
  const [surfaceLodIndices, setSurfaceLodIndices] = useState<number[]>();
  const [inspectingLods, setInspectingLods] = useState(false);
  const wireframeRef = useRef(false);
  useLayoutEffect(() => {
    setPreview(undefined);
    setSelectedToken('');
    setGenerating(false);
    setSettingsOpen(null);
    setRawType(collision?.recipe.rawType ?? defaultCollisionType(project.target.game));
    setProfileSections(
      collision?.recipe.kind === 'hull' && collision.recipe.profileSections > 0
        ? collision.recipe.profileSections : 6,
    );
    setMethod(instancedCollisionMethod(collision));
    setSurfaceLodIndices(entity.asset ? sourceSurfaceLodCache.get(entity.asset.id) : undefined);
    setInspectingLods(false);
    setPainting(false);
    return () => {
      window.clearTimeout(regenerationTimer.current);
      sourceInspection.current += 1;
      generation.current += 1;
      void cancelInstancedCollisionPreview();
    };
  }, [cancelInstancedCollisionPreview, entity.id, project.projectId]);
  useEffect(() => {
    if (!collision) return;
    setRawType(collision.recipe.rawType);
    setMethod(instancedCollisionMethod(collision));
    if (collision.recipe.kind === 'hull' && collision.recipe.profileSections > 0)
      setProfileSections(collision.recipe.profileSections);
  }, [collision?.recipe]);

  const generate = async (
    requestedMethod = method,
    requestedRawType = rawType,
    requestedProfileSections = profileSections,
    apply = false,
  ) => {
    const request = ++generation.current;
    setGenerating(true);
    try {
      const next = await previewInstancedCollision(entity.id, {
        rawType: requestedRawType,
        profileSections: requestedProfileSections,
        surfaceLodIndex: surfaceLodIndex(requestedMethod),
        useHull: requestedMethod === 'hull',
      });
      if (generation.current !== request || next.sourceAssetId !== entity.asset?.id) return;
      const candidate = candidateForMethod(next.candidates, requestedMethod);
      setPreview(next);
      setSelectedToken(candidate?.token ?? '');
      if (apply && candidate && candidate.hardViolationCount === 0 && !combinedUnsafe(candidate))
        await applyInstancedCollisionPreview(candidate.token);
      return next;
    }
    catch {
      // The workspace displays the actionable host error.
      return undefined;
    }
    finally {
      if (generation.current === request) setGenerating(false);
    }
  };
  const scheduleRegeneration = (
    requestedMethod: InstancedCollisionMethod,
    requestedProfileSections = profileSections,
  ) => {
    window.clearTimeout(regenerationTimer.current);
    regenerationTimer.current = window.setTimeout(() => {
      void generate(requestedMethod, rawType, requestedProfileSections, true);
    }, 200);
  };
  const updateRawType = async (nextRawType: number) => {
    setRawType(nextRawType);
    setPreview((current) => current && ({
      ...current,
      candidates: current.candidates.map((candidate) => ({
        ...candidate,
        recipe: { ...candidate.recipe, rawType: nextRawType },
      })),
    }));
    if (!collision) return;
    const updated = await execute({
      id: crypto.randomUUID(),
      kind: 'setInstancedCollisionRawType',
      entityIds: [entity.id],
      rawType: nextRawType,
    });
    if (!updated) setRawType(collision.recipe.rawType);
  };
  const inspectLods = async () => {
    const request = ++sourceInspection.current;
    setInspectingLods(true);
    try {
      const info = await inspectInstancedCollisionSource(entity.id);
      if (sourceInspection.current !== request || info.sourceAssetId !== entity.asset?.id) return;
      sourceSurfaceLodCache.set(info.sourceAssetId, info.surfaceLodIndices);
      setSurfaceLodIndices(info.surfaceLodIndices);
      setMethod((current) => {
        const lodIndex = surfaceLodIndex(current);
        return lodIndex >= 0 && !info.surfaceLodIndices.includes(lodIndex)
          ? 'surface-auto'
          : current;
      });
    }
    catch {
      if (sourceInspection.current === request) setSurfaceLodIndices([]);
    }
    finally {
      if (sourceInspection.current === request) setInspectingLods(false);
    }
  };
  useEffect(() => {
    if (entity.instancedCollisionEnabled === false && surfaceLodIndices === undefined && !inspectingLods)
      void inspectLods();
  }, [entity.instancedCollisionEnabled]);
  const closeSharedCollisionEditor = () => {
    setSettingsOpen(null);
    setPainting(false);
  };
  const setInstancedCollisionEnabled = async (enabled: boolean) => {
    closeSharedCollisionEditor();
    await execute({
      id: crypto.randomUUID(), kind: 'setInstancedCollisionEnabled', entityIds: [entity.id],
      enabled: enabled ? false : null,
    });
  };
  const setInstancedCollisionMode = async (shared: boolean) => {
    closeSharedCollisionEditor();
    if (!shared) {
      await execute({
        id: crypto.randomUUID(), kind: 'setInstancedCollisionEnabled', entityIds: [entity.id], enabled: false,
      });
      return;
    }
    if (entity.instancedCollision) {
      await execute({
        id: crypto.randomUUID(), kind: 'setInstancedCollisionEnabled', entityIds: [entity.id], enabled: true,
      });
      return;
    }
    if (!await execute({
      id: crypto.randomUUID(), kind: 'setInstancedCollisionEnabled', entityIds: [entity.id], enabled: true,
    })) return;
    const next = await generate();
    const requested = next ? candidateForMethod(next.candidates, method) : undefined;
    const automatic = requested
      ? recommendInstancedCollisionCandidate([requested], DEFAULT_MAXIMUM_DEVIATION)
      : undefined;
    if (!automatic || !await applyInstancedCollisionPreview(automatic.token)) {
      setSettingsOpen('settings');
    }
  };
  const selected = preview?.candidates.find((candidate) => candidate.token === selectedToken);
  const setWireframeView = async (checked: boolean) => {
    setWireframe(checked);
    wireframeRef.current = checked;
    if (instancedCollisionOverlay) {
      setInstancedCollisionOverlay({ ...instancedCollisionOverlay, wireframe: checked });
    }
    else if (checked && !collision && !selected) {
      await generate();
    }
  };
  useEffect(() => {
    if (collision) {
      setModelStatus(undefined);
      return;
    }
    if ((entity.instancedCollisionEnabled !== false && settingsOpen !== 'settings') || !selected) {
      setModelStatus(undefined);
      setInstancedCollisionOverlay(undefined);
      return;
    }
    let disposed = false;
    const requestToken = crypto.randomUUID();
    setModelStatus('loading');
    void window.forge.getInstancedCollisionPreviewModel(selected.token, requestToken).then((source) => {
      if (disposed) return;
      setInstancedCollisionOverlay({
        entityId: entity.id,
        candidate: selected,
        url: source.url,
        showSource: true,
        showProxy: true,
        wireframe: wireframeRef.current,
        showOctants: false,
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
  }, [collision?.proxyAssetId, entity.id, entity.instancedCollisionEnabled,
    selected?.token, settingsOpen, setInstancedCollisionOverlay]);

  const pressure = debugInfo && selected ? [...selected.octants]
    .sort((left, right) => right.encodedByteCount - left.encodedByteCount)
    .slice(0, 3) : [];
  const combinedPressure = debugInfo && selected ? [...(selected.combinedAnalysis?.octants ?? [])]
    .sort((left, right) => right.encodedByteCount - left.encodedByteCount)
    .slice(0, 3) : [];
  const matchingInstances = project.entities.filter((candidate) =>
    candidate.asset?.kind === entity.asset?.kind && candidate.asset?.id === entity.asset?.id);
  const sharedCollisionInstanceCount = matchingInstances.filter(
    (candidate) => candidate.instancedCollisionEnabled === true,
  ).length;
  const editingSharedCollision = entity.instancedCollisionEnabled === true && settingsOpen === 'settings';
  const editingCollision = entity.instancedCollisionEnabled === false || editingSharedCollision;
  const toggleSharedCollisionEditor = () => {
    if (editingSharedCollision) {
      closeSharedCollisionEditor();
      return;
    }
    setSettingsOpen('settings');
    if (surfaceLodIndices === undefined && !inspectingLods) void inspectLods();
    if (wireframe && !selected && !generating) void generate();
  };

  return <Fieldset legend="Collision">
    <Stack gap="xs">
      <Checkbox label="Use instanced collision" checked={entity.instancedCollisionEnabled !== undefined}
        disabled={disabled || busy || generating}
        onChange={(event) => void setInstancedCollisionEnabled(event.currentTarget.checked)} />
      <Select label="Mode"
        value={entity.instancedCollisionEnabled === true ? 'shared' : 'individual'}
        data={INSTANCED_COLLISION_MODE_OPTIONS} allowDeselect={false}
        description="Individual edits only this instance; Shared uses the class proxy."
        disabled={disabled || busy || generating || entity.instancedCollisionEnabled === undefined}
        onChange={(value) => value && void setInstancedCollisionMode(value === 'shared')} />
      {generating && <Text size="xs" c="dimmed">Generating collision…</Text>}
      {entity.instancedCollision && <Text size="xs" c="dimmed">
        Shared collision is used by {sharedCollisionInstanceCount.toLocaleString()} of {matchingInstances.length.toLocaleString()} matching {entity.asset?.kind.toLowerCase()} {matchingInstances.length === 1 ? 'instance' : 'instances'}.
      </Text>}
      {entity.instancedCollisionEnabled === true && <Button
        size="xs" variant="light" disabled={disabled || busy || generating}
        onClick={toggleSharedCollisionEditor}>
        {editingSharedCollision ? 'Close shared collision editor' : 'Edit shared collision'}
      </Button>}
      {editingSharedCollision && <Text size="xs" c="yellow" fw={600}>
        Warning: Editing shared collision applies for all instances using shared mode
      </Text>}
      {collision && <Checkbox label="Render instanced collision" checked={renderCollision}
        disabled={disabled}
        onChange={(event) => setInstancedCollisionRendered(entity.id, event.currentTarget.checked)} />}
      {collision && <InstancedCollisionPainter
        entity={entity}
        binding={collision}
        disabled={disabled}
        instanceCount={editingSharedCollision ? sharedCollisionInstanceCount : 1}
        showControls={editingCollision}
        visible={painting || renderCollision}
        active={painting}
        onActiveChange={setPainting}
        wireframe={wireframe}
        onWireframeChange={setWireframeView} />}

      {editingCollision && <Stack gap="xs">
      <Checkbox label="Wireframe view" checked={wireframe} disabled={disabled || generating}
        onChange={(event) => void setWireframeView(event.currentTarget.checked)} />
      <Checkbox label="Debug info" checked={debugInfo}
        onChange={(event) => {
          const checked = event.currentTarget.checked;
          setDebugInfo(checked);
          if (!checked && instancedCollisionOverlay?.showOctants) {
            setInstancedCollisionOverlay({ ...instancedCollisionOverlay, showOctants: false });
          }
        }} />
      {collision
        ? <>
          {debugInfo && <EditorPropertyGrid>
            <EditorProperty label="Proxy"><Code>{collision.proxyAssetId}</Code></EditorProperty>
            <EditorProperty label="Recipe">{formatRecipe(collision.recipe)}</EditorProperty>
            <EditorProperty label="Collision IDs">{formatCollisionType(collision.recipe.rawType, project.target.game)}</EditorProperty>
          </EditorPropertyGrid>}
          <Group>
            <Button size="xs" variant="subtle" color="red" disabled={disabled || busy}
              onClick={() => void execute({
                id: crypto.randomUUID(), kind: 'removeInstancedCollisionProxy', entityIds: [entity.id],
              })}>Remove {editingSharedCollision ? 'shared' : 'individual'} collision</Button>
          </Group>
        </>
        : <>
          <Text size="sm" c="dimmed">No {editingSharedCollision ? 'shared' : 'individual'} collision is assigned.</Text>
          <Button size="xs" variant="light" disabled={disabled || busy || generating}
            onClick={() => void generate(method, rawType, profileSections, true)}>
            Generate {editingSharedCollision ? 'shared' : 'individual'} collision
          </Button>
        </>}

      <Text size="sm" fw={500}>Generation settings</Text>
      <Group grow align="end">
        <Select label="Collision ID" value={String(collisionTypeId(rawType, project.target.game))}
          data={collisionTypeIdOptions(project.target.game)} allowDeselect={false} disabled={disabled || generating}
          onChange={(value) => {
            if (value === null) return;
            const nextRawType = packCollisionType(
              Number(value), soundTypeId(rawType, project.target.game), project.target.game,
            );
            void updateRawType(nextRawType);
          }} />
        <Select label="Sound ID" value={String(soundTypeId(rawType, project.target.game))}
          data={soundTypeIdOptions(project.target.game)} allowDeselect={false} disabled={disabled || generating}
          onChange={(value) => {
            if (value === null) return;
            const nextRawType = packCollisionType(
              collisionTypeId(rawType, project.target.game), Number(value), project.target.game,
            );
            void updateRawType(nextRawType);
          }} />
      </Group>
      <Text size="xs" c="dimmed">Generated faces use {formatCollisionType(rawType, project.target.game)}.</Text>

      <Select label="Collision method"
        value={method} data={INSTANCED_COLLISION_METHOD_OPTIONS.filter((option) => {
          const lodIndex = surfaceLodIndex(option.value as InstancedCollisionMethod);
          return lodIndex < 0 || surfaceLodIndices === undefined || surfaceLodIndices.includes(lodIndex);
        })} allowDeselect={false}
        disabled={disabled || generating || inspectingLods}
        description={inspectingLods ? 'Checking available model LODs…' : undefined}
        onChange={(value) => {
          if (value === null) return;
          const nextMethod = value as InstancedCollisionMethod;
          setMethod(nextMethod);
          setPreview(undefined);
          setSelectedToken('');
          if (collision) void generate(nextMethod, rawType, profileSections, true);
          else if (wireframe) void generate(nextMethod);
        }} />

      {method === 'hull' && <NumberInput label="Shrinkwrap detail"
        description="More sections follow vertical surface changes; redundant sections are removed."
        value={profileSections} min={1} max={16} step={1} allowDecimal={false}
        disabled={disabled || generating}
        onChange={(value) => {
          if (typeof value !== 'number') return;
          const nextProfileSections = Math.max(1, Math.min(16, Math.round(value)));
          setProfileSections(nextProfileSections);
          setPreview(undefined);
          setSelectedToken('');
          if (collision) scheduleRegeneration(method, nextProfileSections);
          else if (wireframe) void generate(method, rawType, nextProfileSections);
        }} />
      }

      {selected && <>
        {debugInfo && <>
          <Text size="xs" fw={500}>Preview: {selected.label}</Text>
          <EditorPropertyGrid>
            <EditorProperty label="Geometry">{selected.faceCount.toLocaleString()} faces · {selected.vertexCount.toLocaleString()} vertices</EditorProperty>
            <EditorProperty label="Octants">{selected.occupiedOctantCount.toLocaleString()} occupied · {selected.duplicateFaceCount.toLocaleString()} duplicated faces</EditorProperty>
            <EditorProperty label="Worst octant">{worstFaces(selected).toLocaleString()} faces · {instancedCollisionWorstOctantBytes(selected).toLocaleString()} bytes</EditorProperty>
            <EditorProperty label="Soft pressure">Unqualified · hard limits only</EditorProperty>
            <EditorProperty label="Max deviation">{selected.maximumDeviation.toFixed(3)} units ({selected.deviationSampleCount.toLocaleString()} samples)</EditorProperty>
            <EditorProperty label="Encoded size">{formatBytes(selected.encodedByteCount)}</EditorProperty>
            <EditorProperty label="Collision IDs">{formatCollisionType(selected.recipe.rawType, project.target.game)}</EditorProperty>
            {selected.combinedAnalysis && <>
              <EditorProperty label="Project instances">{selected.combinedAnalysis.instanceCount.toLocaleString()} enabled</EditorProperty>
              <EditorProperty label="Combined collision">{selected.combinedAnalysis.logicalFaceCount.toLocaleString()} faces · {selected.combinedAnalysis.occupiedOctantCount.toLocaleString()} octants</EditorProperty>
              <EditorProperty label="Combined worst octant">{Math.max(0, ...selected.combinedAnalysis.octants.map((octant) => octant.faceCount)).toLocaleString()} faces · {Math.max(0, ...selected.combinedAnalysis.octants.map((octant) => octant.encodedByteCount)).toLocaleString()} bytes</EditorProperty>
            </>}
          </EditorPropertyGrid>
          {pressure.length > 0 && <OctantPressureDetails title="Highest candidate pressure" octants={pressure} />}
          {combinedPressure.length > 0
            && <OctantPressureDetails title="Highest combined pressure" octants={combinedPressure} />}
          {instancedCollisionOverlay?.candidate?.token === selected.token && <Group>
            <Checkbox label="Source" checked={instancedCollisionOverlay.showSource}
              onChange={(event) => setInstancedCollisionOverlay({
                ...instancedCollisionOverlay, showSource: event.currentTarget.checked,
              })} />
            <Checkbox label="Proxy" checked={instancedCollisionOverlay.showProxy}
              onChange={(event) => setInstancedCollisionOverlay({
                ...instancedCollisionOverlay, showProxy: event.currentTarget.checked,
              })} />
            <Checkbox label="Octant pressure" checked={instancedCollisionOverlay.showOctants}
              onChange={(event) => setInstancedCollisionOverlay({
                ...instancedCollisionOverlay, showOctants: event.currentTarget.checked,
              })} />
          </Group>}
        </>}
        {selected.maximumDeviation > DEFAULT_MAXIMUM_DEVIATION && <Text size="xs" c="yellow">
          This candidate exceeds the {DEFAULT_MAXIMUM_DEVIATION.toFixed(3)} unit recommendation threshold.
        </Text>}
        {selected.hardViolationCount > 0 && <Text size="xs" c="red">
          This collision has {selected.hardViolationCount} hard limit failures.
        </Text>}
        {selected.combinedAnalysis?.error && <Text size="xs" c="red">
          Combined project analysis failed: {selected.combinedAnalysis.error}
        </Text>}
        {selected.combinedAnalysis && selected.combinedAnalysis.hardViolationCount > 0
          && !selected.combinedAnalysis.error && <Text size="xs" c="red">
            Combined project collision has {selected.combinedAnalysis.hardViolationCount} hard limit failures.
          </Text>}
        {modelStatus === 'loading' && <Text size="xs" c="dimmed">Preparing viewport overlay…</Text>}
        {modelStatus === 'failed' && <Text size="xs" c="yellow">Viewport overlay unavailable.</Text>}
      </>}
      </Stack>}
    </Stack>
  </Fieldset>;
}

function OctantPressureDetails({ title, octants }: {
  title: string;
  octants: EditorCollisionOctantCost[];
}) {
  return <Stack gap={4}>
    <Text size="xs" fw={500}>{title}</Text>
    <Accordion multiple variant="contained">
      {octants.map((octant) => <Accordion.Item
        key={`${octant.x}:${octant.y}:${octant.z}`}
        value={`${octant.x}:${octant.y}:${octant.z}`}>
        <Accordion.Control>
          <Text size="xs">Octant ({octant.x}, {octant.y}, {octant.z}) · {octant.encodedByteCount.toLocaleString()} B · {octant.faceCount.toLocaleString()} faces</Text>
        </Accordion.Control>
        <Accordion.Panel>
          <Stack gap={2}>
            <Text size="xs">{octant.vertexCount.toLocaleString()} vertices · {octant.quadCount.toLocaleString()} quads</Text>
            {octant.additionIds?.length
              ? <Text size="xs">Entities: {octant.additionIds.join(', ')}</Text>
              : null}
            {octant.violations.length
              ? <Text size="xs" c="red">{octant.violations.join(' ')}</Text>
              : <Text size="xs" c="dimmed">No hard-limit violations.</Text>}
          </Stack>
        </Accordion.Panel>
      </Accordion.Item>)}
    </Accordion>
  </Stack>;
}

function worstFaces(candidate: EditorInstancedCollisionPreview['candidates'][number]): number {
  return Math.max(0, ...candidate.octants.map((octant) => octant.faceCount));
}

function combinedUnsafe(candidate: EditorInstancedCollisionPreview['candidates'][number]): boolean {
  return Boolean(candidate.combinedAnalysis?.error || candidate.combinedAnalysis?.hardViolationCount);
}

function instancedCollisionMethod(collision: EditorEntity['instancedCollision']): InstancedCollisionMethod {
  const recipe = collision?.recipe;
  if (!recipe) return 'surface-auto';
  if (recipe.kind !== 'surface') return 'hull';
  if (recipe.lodIndex === 0) return 'surface-0';
  if (recipe.lodIndex === 1) return 'surface-1';
  if (recipe.lodIndex === 2) return 'surface-2';
  return 'surface-auto';
}

function surfaceLodIndex(method: InstancedCollisionMethod): number {
  if (method === 'surface-0') return 0;
  if (method === 'surface-1') return 1;
  if (method === 'surface-2') return 2;
  return -1;
}

function candidateForMethod(
  candidates: EditorInstancedCollisionCandidate[],
  method: InstancedCollisionMethod,
): EditorInstancedCollisionCandidate | undefined {
  const preset = method === 'hull' ? 'solidHull' : 'surface';
  return candidates.find((candidate) => candidate.preset === preset);
}

function formatRecipe(recipe: NonNullable<EditorEntity['instancedCollision']>['recipe']): string {
  const lod = ['High LOD', 'Med LOD', 'Low LOD'][recipe.lodIndex] ?? `LOD ${recipe.lodIndex}`;
  if (recipe.kind === 'surface') return `Decimated mesh · ${lod}`;
  if (recipe.kind === 'hull') return `Shrinkwrap · ${Math.max(1, recipe.profileSections)} sections · ${lod}`;
  return `Custom wrap · ${recipe.detailSize} detail · seals ${recipe.sealOpeningSize}`;
}

function formatBytes(value: number): string {
  return value < 1024 ? `${value} B` : `${(value / 1024).toFixed(1)} KiB`;
}

import { Accordion, Button, Checkbox, Code, Fieldset, Group, NumberInput, Select, Stack, Text } from '@mantine/core';
import { useEffect, useLayoutEffect, useRef, useState } from 'react';

import type {
  EditorCollisionOctantCost, EditorEntity, EditorTieCollisionCandidate, EditorTieCollisionPreview,
} from '../../types/EditorRuntime.js';
import {
  collisionTypeId, collisionTypeIdOptions, defaultCollisionType, formatCollisionType,
  packCollisionType, soundTypeId, soundTypeIdOptions,
} from '../../utils/CollisionFormat.ts';
import {
  recommendTieCollisionCandidate, tieCollisionWorstOctantBytes,
} from '../../utils/TieCollision.ts';
import { useEditor } from './EditorContext.ts';
import { EditorProperty, EditorPropertyGrid } from './EditorPrimitives.tsx';

const TIE_COLLISION_METHOD_OPTIONS = [
  { value: 'surface-auto', label: 'Decimate (Automatic LOD)' },
  { value: 'surface-0', label: 'Decimate LOD 0' },
  { value: 'surface-1', label: 'Decimate LOD 1' },
  { value: 'surface-2', label: 'Decimate LOD 2' },
  { value: 'hull', label: 'Shrinkwrap' },
];
const DEFAULT_MAXIMUM_DEVIATION = 4;
type TieCollisionMethod = 'surface-auto' | 'surface-0' | 'surface-1' | 'surface-2' | 'hull';
const tieSurfaceLodCache = new Map<string, number[]>();

export function TieCollisionProperties({ entity, disabled }: { entity: EditorEntity; disabled: boolean }) {
  const {
    applyTieCollisionPreview, busy, cancelTieCollisionPreview, execute, inspectTieCollisionSource,
    previewTieCollision, project,
    setTieCollisionOverlay, tieCollisionOverlay,
  } = useEditor();
  const [preview, setPreview] = useState<EditorTieCollisionPreview>();
  const [selectedToken, setSelectedToken] = useState('');
  const [generating, setGenerating] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState<string | null>(null);
  const [modelStatus, setModelStatus] = useState<'loading' | 'ready' | 'failed'>();
  const [rawType, setRawType] = useState(
    entity.tieCollision?.recipe.rawType ?? defaultCollisionType(project.target.game),
  );
  const [profileSections, setProfileSections] = useState(
    entity.tieCollision?.recipe.kind === 'hull' && entity.tieCollision.recipe.profileSections > 0
      ? entity.tieCollision.recipe.profileSections : 6,
  );
  const [method, setMethod] = useState<TieCollisionMethod>(tieCollisionMethod(entity));
  const generation = useRef(0);
  const sourceInspection = useRef(0);
  const regenerationTimer = useRef<number | undefined>(undefined);
  const [wireframe, setWireframe] = useState(false);
  const [debugInfo, setDebugInfo] = useState(false);
  const [surfaceLodIndices, setSurfaceLodIndices] = useState<number[]>();
  const [inspectingLods, setInspectingLods] = useState(false);
  const wireframeRef = useRef(false);
  useLayoutEffect(() => {
    setPreview(undefined);
    setSelectedToken('');
    setGenerating(false);
    setSettingsOpen(null);
    setRawType(entity.tieCollision?.recipe.rawType ?? defaultCollisionType(project.target.game));
    setProfileSections(
      entity.tieCollision?.recipe.kind === 'hull' && entity.tieCollision.recipe.profileSections > 0
        ? entity.tieCollision.recipe.profileSections : 6,
    );
    setMethod(tieCollisionMethod(entity));
    setSurfaceLodIndices(entity.asset ? tieSurfaceLodCache.get(entity.asset.id) : undefined);
    setInspectingLods(false);
    return () => {
      window.clearTimeout(regenerationTimer.current);
      sourceInspection.current += 1;
      generation.current += 1;
      void cancelTieCollisionPreview();
    };
  }, [cancelTieCollisionPreview, entity.id]);
  useEffect(() => {
    if (!entity.tieCollision) return;
    setRawType(entity.tieCollision.recipe.rawType);
    setMethod(tieCollisionMethod(entity));
    if (entity.tieCollision.recipe.kind === 'hull' && entity.tieCollision.recipe.profileSections > 0)
      setProfileSections(entity.tieCollision.recipe.profileSections);
  }, [entity.tieCollision?.recipe]);

  const generate = async (
    requestedMethod = method,
    requestedRawType = rawType,
    requestedProfileSections = profileSections,
    apply = false,
  ) => {
    const request = ++generation.current;
    setGenerating(true);
    try {
      const next = await previewTieCollision(entity.id, {
        rawType: requestedRawType,
        profileSections: requestedProfileSections,
        surfaceLodIndex: surfaceLodIndex(requestedMethod),
        useHull: requestedMethod === 'hull',
      });
      if (generation.current !== request || next.tieAssetId !== entity.asset?.id) return;
      const candidate = candidateForMethod(next.candidates, requestedMethod);
      setPreview(next);
      setSelectedToken(candidate?.token ?? '');
      if (apply && candidate && candidate.hardViolationCount === 0 && !combinedUnsafe(candidate))
        await applyTieCollisionPreview(candidate.token);
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
    requestedMethod: TieCollisionMethod,
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
    if (!entity.tieCollision) return;
    const updated = await execute({
      id: crypto.randomUUID(),
      kind: 'setTieCollisionRawType',
      entityIds: [entity.id],
      rawType: nextRawType,
    });
    if (!updated) setRawType(entity.tieCollision.recipe.rawType);
  };
  const inspectLods = async () => {
    const request = ++sourceInspection.current;
    setInspectingLods(true);
    try {
      const info = await inspectTieCollisionSource(entity.id);
      if (sourceInspection.current !== request || info.tieAssetId !== entity.asset?.id) return;
      tieSurfaceLodCache.set(info.tieAssetId, info.surfaceLodIndices);
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
  const setInstancedCollision = async (enabled: boolean) => {
    if (!enabled || entity.tieCollision) {
      await execute({
        id: crypto.randomUUID(), kind: 'setTieCollisionEnabled', entityIds: [entity.id], enabled,
      });
      return;
    }
    const next = await generate();
    const requested = next ? candidateForMethod(next.candidates, method) : undefined;
    const automatic = requested
      ? recommendTieCollisionCandidate([requested], DEFAULT_MAXIMUM_DEVIATION)
      : undefined;
    if (!automatic || !await applyTieCollisionPreview(automatic.token)) {
      setSettingsOpen('settings');
      return;
    }
  };
  const selected = preview?.candidates.find((candidate) => candidate.token === selectedToken);
  const setWireframeView = async (checked: boolean) => {
    setWireframe(checked);
    wireframeRef.current = checked;
    if (tieCollisionOverlay) {
      setTieCollisionOverlay({ ...tieCollisionOverlay, wireframe: checked });
    }
    else if (checked && !selected) {
      await generate();
    }
  };
  useEffect(() => {
    if (!selected) {
      setModelStatus(undefined);
      setTieCollisionOverlay(undefined);
      return;
    }
    let disposed = false;
    const requestToken = crypto.randomUUID();
    setModelStatus('loading');
    void window.forge.getTieCollisionPreviewModel(selected.token, requestToken).then((source) => {
      if (disposed) return;
      setTieCollisionOverlay({
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
      setTieCollisionOverlay(undefined);
    };
  }, [entity.id, selected?.token, setTieCollisionOverlay]);
  const pressure = debugInfo && selected ? [...selected.octants]
    .sort((left, right) => right.encodedByteCount - left.encodedByteCount)
    .slice(0, 3) : [];
  const combinedPressure = debugInfo && selected ? [...(selected.combinedAnalysis?.octants ?? [])]
    .sort((left, right) => right.encodedByteCount - left.encodedByteCount)
    .slice(0, 3) : [];
  const matchingTieInstances = project.entities.filter((candidate) =>
    candidate.asset?.kind === 'Tie' && candidate.asset.id === entity.asset?.id);
  const sharedCollisionInstanceCount = matchingTieInstances.filter(
    (candidate) => candidate.tieCollisionEnabled === true,
  ).length;

  return <Fieldset legend="Collision">
    <Stack gap="xs">
      <Checkbox label="Use shared collision" checked={entity.tieCollisionEnabled === true}
        disabled={disabled || busy || generating}
        onChange={(event) => void setInstancedCollision(event.currentTarget.checked)} />
      {generating && <Text size="xs" c="dimmed">Generating shared collision…</Text>}
      {entity.tieCollision && <Text size="xs" c="dimmed">
        Used by {sharedCollisionInstanceCount.toLocaleString()} of {matchingTieInstances.length.toLocaleString()} matching TIE {matchingTieInstances.length === 1 ? 'instance' : 'instances'}.
      </Text>}

      <Accordion value={settingsOpen} onChange={(value) => {
        setSettingsOpen(value);
        if (value === 'settings' && surfaceLodIndices === undefined && !inspectingLods)
          void inspectLods();
        if (value === 'settings' && wireframe && !selected && !generating) void generate();
      }} variant="contained">
        <Accordion.Item value="settings">
          <Accordion.Control>Shared collision settings</Accordion.Control>
          <Accordion.Panel>
            <Stack gap="xs">
      <Checkbox label="Wireframe view" checked={wireframe} disabled={disabled || generating}
        onChange={(event) => void setWireframeView(event.currentTarget.checked)} />
      <Checkbox label="Debug info" checked={debugInfo}
        onChange={(event) => {
          const checked = event.currentTarget.checked;
          setDebugInfo(checked);
          if (!checked && tieCollisionOverlay?.showOctants) {
            setTieCollisionOverlay({ ...tieCollisionOverlay, showOctants: false });
          }
        }} />
      {entity.tieCollision
        ? <>
          {debugInfo && <EditorPropertyGrid>
            <EditorProperty label="Proxy"><Code>{entity.tieCollision.proxyAssetId}</Code></EditorProperty>
            <EditorProperty label="Recipe">{formatRecipe(entity.tieCollision.recipe)}</EditorProperty>
            <EditorProperty label="Collision IDs">{formatCollisionType(entity.tieCollision.recipe.rawType, project.target.game)}</EditorProperty>
          </EditorPropertyGrid>}
          <Group>
            <Button size="xs" variant="subtle" color="red" disabled={disabled || busy}
              onClick={() => void execute({
                id: crypto.randomUUID(), kind: 'removeTieCollisionProxy', entityIds: [entity.id],
              })}>Remove shared collision</Button>
          </Group>
        </>
        : <Text size="sm" c="dimmed">No shared collision is assigned to this TIE asset.</Text>}

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
        value={method} data={TIE_COLLISION_METHOD_OPTIONS.filter((option) => {
          const lodIndex = surfaceLodIndex(option.value as TieCollisionMethod);
          return lodIndex < 0 || surfaceLodIndices === undefined || surfaceLodIndices.includes(lodIndex);
        })} allowDeselect={false}
        disabled={disabled || generating || inspectingLods}
        description={inspectingLods ? 'Checking available model LODs…' : undefined}
        onChange={(value) => {
          if (value === null) return;
          const nextMethod = value as TieCollisionMethod;
          setMethod(nextMethod);
          setPreview(undefined);
          setSelectedToken('');
          if (entity.tieCollision) void generate(nextMethod, rawType, profileSections, true);
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
          if (entity.tieCollision) scheduleRegeneration(method, nextProfileSections);
          else if (wireframe) void generate(method, rawType, nextProfileSections);
        }} />
      }

      {selected && <>
        {debugInfo && <>
          <Text size="xs" fw={500}>Preview: {selected.label}</Text>
          <EditorPropertyGrid>
            <EditorProperty label="Geometry">{selected.faceCount.toLocaleString()} faces · {selected.vertexCount.toLocaleString()} vertices</EditorProperty>
            <EditorProperty label="Octants">{selected.occupiedOctantCount.toLocaleString()} occupied · {selected.duplicateFaceCount.toLocaleString()} duplicated faces</EditorProperty>
            <EditorProperty label="Worst octant">{worstFaces(selected).toLocaleString()} faces · {tieCollisionWorstOctantBytes(selected).toLocaleString()} bytes</EditorProperty>
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
          {tieCollisionOverlay?.candidate.token === selected.token && <Group>
            <Checkbox label="Source" checked={tieCollisionOverlay.showSource}
              onChange={(event) => setTieCollisionOverlay({
                ...tieCollisionOverlay, showSource: event.currentTarget.checked,
              })} />
            <Checkbox label="Proxy" checked={tieCollisionOverlay.showProxy}
              onChange={(event) => setTieCollisionOverlay({
                ...tieCollisionOverlay, showProxy: event.currentTarget.checked,
              })} />
            <Checkbox label="Octant pressure" checked={tieCollisionOverlay.showOctants}
              onChange={(event) => setTieCollisionOverlay({
                ...tieCollisionOverlay, showOctants: event.currentTarget.checked,
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
            </Stack>
          </Accordion.Panel>
        </Accordion.Item>
      </Accordion>
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

function worstFaces(candidate: EditorTieCollisionPreview['candidates'][number]): number {
  return Math.max(0, ...candidate.octants.map((octant) => octant.faceCount));
}

function combinedUnsafe(candidate: EditorTieCollisionPreview['candidates'][number]): boolean {
  return Boolean(candidate.combinedAnalysis?.error || candidate.combinedAnalysis?.hardViolationCount);
}

function tieCollisionMethod(entity: EditorEntity): TieCollisionMethod {
  const recipe = entity.tieCollision?.recipe;
  if (!recipe) return 'surface-auto';
  if (recipe.kind !== 'surface') return 'hull';
  if (recipe.lodIndex === 0) return 'surface-0';
  if (recipe.lodIndex === 1) return 'surface-1';
  if (recipe.lodIndex === 2) return 'surface-2';
  return 'surface-auto';
}

function surfaceLodIndex(method: TieCollisionMethod): number {
  if (method === 'surface-0') return 0;
  if (method === 'surface-1') return 1;
  if (method === 'surface-2') return 2;
  return -1;
}

function candidateForMethod(
  candidates: EditorTieCollisionCandidate[],
  method: TieCollisionMethod,
): EditorTieCollisionCandidate | undefined {
  const preset = method === 'hull' ? 'solidHull' : 'surface';
  return candidates.find((candidate) => candidate.preset === preset);
}

function formatRecipe(recipe: NonNullable<EditorEntity['tieCollision']>['recipe']): string {
  if (recipe.kind === 'surface') return `Decimated mesh · LOD ${recipe.lodIndex}`;
  if (recipe.kind === 'hull') return `Shrinkwrap · ${Math.max(1, recipe.profileSections)} sections · LOD ${recipe.lodIndex}`;
  return `Custom wrap · ${recipe.detailSize} detail · seals ${recipe.sealOpeningSize}`;
}

function formatBytes(value: number): string {
  return value < 1024 ? `${value} B` : `${(value / 1024).toFixed(1)} KiB`;
}

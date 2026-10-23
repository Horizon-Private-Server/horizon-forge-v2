import { Badge, Button, Code, Group, SegmentedControl, Select, Stack, Text } from '@mantine/core';
import { useEffect, useRef, useState } from 'react';

import type { AssetExplorerCategory, AssetExplorerItem, AssetPreviewKind } from '../../types/AssetExplorer.js';
import type { AssetPreviewSource, CustomModelPreviewDetails } from '../../types/AssetExplorer.js';
import { createAssetPlacementCommand, createSkyShellAddCommand } from '../../utils/AssetPlacement.ts';
import { contentOriginLabel } from '../../utils/ContentOrigin.ts';
import { parseCustomModelPreviewDetails } from '../../utils/CustomModelPreview.ts';
import { errorMessage } from '../../utils/Errors.ts';
import { applyTextureChannel, type TextureChannel } from '../../utils/TexturePreview.ts';
import { AssetModelPreview } from './AssetModelPreview.tsx';
import { useEditor } from './EditorContext.ts';
import { EditorEmptyState, EditorPanel } from './EditorPrimitives.tsx';
import { MobySoundButtons } from './MobySoundButtons.tsx';

export function AssetPreviewPanel() {
  const { assetPreview: family, busy, execute, inspectAsset, openImportBatch, project } = useEditor();
  const [assetId, setAssetId] = useState('');
  useEffect(() => setAssetId(family?.representativeAssetId ?? ''), [family]);
  if (!family) return <EditorPanel label="Asset Preview">
    <EditorEmptyState message="Select an asset to inspect it." />
  </EditorPanel>;
  const item = family.variants.find((variant) => variant.assetId === assetId)
    ?? family.variants.find((variant) => variant.assetId === family.representativeAssetId)
    ?? family.variants[0];
  const kind = previewKind(item);
  const customModel = kind === 'tie' && item.tags.includes('custom-model');
  const customInstanceCount = customModel
    ? project.entities.filter((entity) => entity.asset?.id === item.assetId).length
    : 0;
  const levels = [...new Set(family.variants.flatMap((variant) => variant.sources.map((source) => source.level)))].sort();
  const projectUses = [...new Map(project.entities
    .filter((entity) => entity.asset?.id === item.assetId)
    .map((entity) => {
      const label = contentOriginLabel(entity.origin, entity.provenance?.level);
      const batch = entity.origin?.importBatchId;
      return [`${label}:${batch ?? ''}`, { label, batch }] as const;
    })).values()];
  return <EditorPanel label="Asset Preview">
    <Stack>
      {kind === 'tie' && item.tags.includes('custom-model')
        ? <CustomModelPreview
            assetId={item.assetId} label={item.displayLabel} previewRevision={item.previewRevision}
          />
        : kind === 'texture' && item.previewState !== 'missingBlob'
        ? <AssetTexturePreview assetId={item.assetId} label={item.displayLabel} />
        : kind && kind !== 'texture' && item.previewState !== 'missingBlob'
          ? <AssetModelPreview
              assetId={item.assetId} kind={kind} label={item.displayLabel} shellIndex={item.shellIndex}
            />
          : <EditorEmptyState message="Interactive preview unavailable for this asset." />}
      <Text fw={600}>{family.displayLabel}</Text>
      <Group gap="xs">
        <Badge variant="light">{categoryLabel(family.category)}</Badge>
        <Badge variant="light">{family.variants.length} exact {family.variants.length === 1 ? 'variant' : 'variants'}</Badge>
        <Badge color={item.canPlace ? 'teal' : 'gray'} variant="light">
          {item.canPlace ? 'Compatible' : 'Preview only'}
        </Badge>
      </Group>
      {family.variants.length > 1 && <Select
        aria-label="Exact source variant"
        data={family.variants.map((variant) => ({
          value: variant.assetId,
          label: `${[...new Set(variant.sources.map((source) => source.level))].join(', ')} · ${variant.assetId.slice(0, 12)}`,
        }))}
        label="Exact source variant"
        value={item.assetId}
        onChange={(value) => { if (value) setAssetId(value); }}
      />}
      {levels.length > 0 && <Text size="sm" c="dimmed">Sources: {levels.join(', ')}</Text>}
      {projectUses.map((use) => <Text key={`${use.label}:${use.batch ?? ''}`} size="sm" c="dimmed">
        Project use: {use.label}{use.batch && <> · batch <Button
          p={0}
          size="compact-xs"
          variant="transparent"
          onClick={() => openImportBatch(use.batch!)}
        ><Code>{use.batch}</Code></Button></>}
      </Text>)}
      <Text size="xs" c="dimmed">Asset ID</Text><Code className="asset-explorer-id">{item.assetId}</Code>
      {!item.tags.includes('project-attached') && <Text size="sm">
        Canonical format {item.canonicalFormatVersion} · {item.byteSize.toLocaleString()} bytes
      </Text>}
      {item.classIds.length > 0 && <Text size="sm">
        Class IDs: {item.classIds.map((value) => `0x${value.toString(16).padStart(4, '0')}`).join(', ')}
      </Text>}
      {kind === 'moby' && family.classId !== undefined && <MobySoundButtons
        assetId={item.assetId}
        classId={family.classId}
        projectId={project.projectId}
        sources={item.sources}
      />}
      {item.shellIndex !== undefined && <Text size="sm">Shell index: {item.shellIndex}</Text>}
      {item.aliases.length > 0 && <Text size="sm">Aliases: {item.aliases.join(', ')}</Text>}
      {item.tags.length > 0 && <Group gap="xs">{item.tags.map((tag) => <Badge key={tag}>{tag}</Badge>)}</Group>}
      {item.sources.map((source) => <Text
        key={`${source.game}:${source.region}:${source.revision}:${source.level}:${source.archive}:${source.sourceIndex}:${
          source.textureUse ? `${source.textureUse.ownerKind}:${source.textureUse.ownerClassId}:${source.textureUse.role}:${source.textureUse.slot}` : ''}`}
        size="sm"
      >
        {source.game} · {source.region} · {source.revision} · Level {source.level}
        {source.textureUse && ` · ${source.textureUse.ownerKind} 0x${source.textureUse.ownerClassId
          .toString(16).padStart(4, '0')} · ${source.textureUse.role} ${source.textureUse.slot} · ${
          source.textureUse.restorable ? 'restorable' : 'placeholder, non-restorable'}`}
      </Text>)}
      {!item.canPlace && <Text c="dimmed" size="sm">{item.placementDisabledReason}</Text>}
      {kind && kind !== 'texture' && kind !== 'sky' && kind !== 'tfrag'
        && family.classId !== undefined && <Button
        disabled={busy || !item.canPlace}
        title={item.canPlace ? 'Place at project origin' : item.placementDisabledReason}
        onClick={() => void execute(createAssetPlacementCommand({
          assetId: item.assetId, kind, classId: family.classId!,
        }, { x: 0, y: 0, z: 0 }))}
      >Place at origin</Button>}
      {customModel && <Button
        color="red"
        disabled={busy || customInstanceCount > 0}
        title={customInstanceCount > 0
          ? `Delete its ${customInstanceCount} scene ${customInstanceCount === 1 ? 'instance' : 'instances'} first.`
          : 'Delete this custom TIE from the project'}
        variant="light"
        onClick={() => {
          if (!window.confirm(`Delete custom TIE "${item.displayLabel}" from this project?`)) return;
          void execute({
            id: crypto.randomUUID(), kind: 'deleteCustomTie', entityIds: [], modelAssetId: item.assetId,
          }).then((deleted) => { if (deleted) inspectAsset(undefined, false); });
        }}
      >Delete custom TIE</Button>}
      {kind === 'sky' && item.shellIndex !== undefined && <Button
        disabled={busy || !item.canPlace}
        title={item.canPlace ? 'Append this shell to the project sky' : item.placementDisabledReason}
        onClick={() => void execute(createSkyShellAddCommand({
          assetId: item.assetId, kind: 'sky', shellIndex: item.shellIndex!,
        }))}
      >Add to sky</Button>}
    </Stack>
  </EditorPanel>;
}

function CustomModelPreview({ assetId, label, previewRevision }: {
  assetId: string;
  label: string;
  previewRevision?: string;
}) {
  const [view, setView] = useState<'source' | 'target'>('target');
  const [source, setSource] = useState<AssetPreviewSource>();
  const [details, setDetails] = useState<CustomModelPreviewDetails>();
  const [detailsError, setDetailsError] = useState('');
  const [primitive, setPrimitive] = useState('0');
  useEffect(() => {
    setSource(undefined);
    setDetails(undefined);
    setDetailsError('');
  }, [assetId, previewRevision, view]);
  useEffect(() => {
    if (!source?.detailsUrl) return;
    const cancellation = new AbortController();
    void fetch(source.detailsUrl, { signal: cancellation.signal })
      .then((response) => {
        if (!response.ok) throw new Error(`Preview metadata returned ${response.status}`);
        return response.json() as Promise<unknown>;
      })
      .then(parseCustomModelPreviewDetails)
      .then(setDetails)
      .catch((cause: unknown) => {
        if (!cancellation.signal.aborted) setDetailsError(errorMessage(cause));
      });
    return () => cancellation.abort();
  }, [source?.detailsUrl]);
  const selected = details?.primitives[Number(primitive)] ?? details?.primitives[0];
  return <Stack gap="xs">
    <SegmentedControl
      aria-label="Custom model preview view"
      data={[{ label: 'Source / canonical', value: 'source' }, { label: 'Native target', value: 'target' }]}
      fullWidth
      value={view}
      onChange={(value) => setView(value as 'source' | 'target')}
    />
    <Text c="dimmed" size="xs">
      {view === 'source'
        ? 'Original imported glTF/GLB retained by the project.'
        : 'Semantic re-read of the converted UYA TIE bytes that will be packed.'}
    </Text>
    <AssetModelPreview
      assetId={assetId}
      customView={view}
      kind="tie"
      label={`${label} ${view} view`}
      onSource={setSource}
      previewRevision={previewRevision}
    />
    {detailsError && <Text c="red" role="alert" size="sm">{detailsError}</Text>}
    {details && <Stack gap="xs">
      <Group gap="xs">
        <Badge variant="light">{details.vertexCount.toLocaleString()} vertices</Badge>
        <Badge variant="light">{details.triangleCount.toLocaleString()} triangles</Badge>
        <Badge variant="light">{details.packetCount.toLocaleString()} packets</Badge>
        <Badge variant="light">{details.materialCount} materials</Badge>
        <Badge variant="light">{details.textureCount} textures</Badge>
        <Badge variant="light">{details.paletteCount} palettes</Badge>
      </Group>
      <Text size="sm">Bounds: {formatBounds(details)}</Text>
      <Text size="sm">LOD: {details.lodPolicy}</Text>
      {details.primitives.length > 0 && <Select
        aria-label="Source primitive to target material mapping"
        data={details.primitives.map((mapping, index) => ({
          value: String(index),
          label: `Mesh ${mapping.sourceMeshIndex}, primitive ${mapping.sourcePrimitiveIndex} → material ${mapping.nativeMaterialIndex}`,
        }))}
        label="Primitive / material mapping"
        value={primitive}
        onChange={(value) => setPrimitive(value ?? '0')}
      />}
      {selected && <Text size="sm">
        Source mesh {selected.sourceMeshIndex}, primitive {selected.sourcePrimitiveIndex} → native material {
          selected.nativeMaterialIndex}, triangles {selected.firstTriangle}–{
          selected.firstTriangle + Math.max(0, selected.triangleCount - 1)}, packets {selected.firstPacket}–{
          selected.firstPacket + Math.max(0, selected.packetCount - 1)}
      </Text>}
      {source?.diagnosticGlbUrl && <Button
        variant="default"
        onClick={() => void window.forge.exportAssetPreviewDiagnostic(
          source.diagnosticGlbUrl!, details.sourceName,
        ).catch((cause: unknown) => setDetailsError(errorMessage(cause)))}
      >Export diagnostic GLB</Button>}
      <Text c="dimmed" size="xs">
        The diagnostic GLB is deterministic and embeds conversion labels, but is not an authoritative project asset.
      </Text>
    </Stack>}
  </Stack>;
}

function formatBounds(details: CustomModelPreviewDetails): string {
  const value = details.bounds;
  const format = (number: number) => Number(number.toFixed(3)).toString();
  return `[${format(value.minimumX)}, ${format(value.minimumY)}, ${format(value.minimumZ)}]–[${
    format(value.maximumX)}, ${format(value.maximumY)}, ${format(value.maximumZ)}]`;
}

function previewKind(item: AssetExplorerItem): AssetPreviewKind | undefined {
  if (item.category === 'ties') return 'tie';
  if (item.category === 'shrubs') return 'shrub';
  if (item.category === 'mobys') return 'moby';
  if (item.category === 'tfrags') return 'tfrag';
  if (item.category === 'textures') return 'texture';
  if (item.category === 'skyShells') return 'sky';
  return undefined;
}

function AssetTexturePreview({ assetId, label }: { assetId: string; label: string }) {
  const [source, setSource] = useState('');
  const [image, setImage] = useState<ImageBitmap>();
  const [channel, setChannel] = useState<TextureChannel>('rgba');
  const [dimensions, setDimensions] = useState('');
  const [error, setError] = useState('');
  const canvas = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const requestToken = crypto.randomUUID();
    let disposed = false;
    setSource('');
    setDimensions('');
    setError('');
    void window.forge.getAssetPreview(assetId, 'texture', requestToken)
      .then((value) => { if (!disposed) setSource(value.url); })
      .catch((cause: unknown) => { if (!disposed) setError(errorMessage(cause)); });
    return () => {
      disposed = true;
      void window.forge.cancelAssetPreview(requestToken);
    };
  }, [assetId]);
  useEffect(() => {
    if (!source) {
      setImage(undefined);
      return;
    }
    const cancellation = new AbortController();
    let disposed = false;
    let bitmap: ImageBitmap | undefined;
    void fetch(source, { signal: cancellation.signal })
      .then((response) => {
        if (!response.ok) throw new Error(`Texture preview returned ${response.status}`);
        return response.blob();
      })
      .then(createImageBitmap)
      .then((value) => {
        if (disposed) {
          value.close();
          return;
        }
        bitmap = value;
        setDimensions(`${value.width} × ${value.height}`);
        setImage(value);
      })
      .catch((cause: unknown) => {
        if (!cancellation.signal.aborted) setError(errorMessage(cause));
      });
    return () => {
      disposed = true;
      cancellation.abort();
      bitmap?.close();
    };
  }, [source]);
  useEffect(() => {
    const target = canvas.current;
    if (!target || !image) return;
    target.width = image.width;
    target.height = image.height;
    const context = target.getContext('2d');
    if (!context) return;
    context.imageSmoothingEnabled = false;
    context.drawImage(image, 0, 0);
    if (channel !== 'rgba') {
      const pixels = context.getImageData(0, 0, image.width, image.height);
      applyTextureChannel(pixels.data, channel);
      context.putImageData(pixels, 0, 0);
    }
  }, [channel, image]);
  return <Stack gap="xs">
    <div className="asset-texture-preview">
      {source && <canvas ref={canvas} aria-label={`${label} texture preview`} role="img" />}
      {!image && !error && <Text c="dimmed" role="status" size="sm">Loading texture…</Text>}
      {error && <Text c="red" role="alert" size="sm">{error}</Text>}
      {dimensions && <Text className="asset-texture-preview-size" size="xs">{dimensions}</Text>}
    </div>
    <SegmentedControl
      aria-label="Texture channels"
      data={[
        { label: 'RGBA', value: 'rgba' }, { label: 'RGB', value: 'rgb' },
        { label: 'R', value: 'red' }, { label: 'G', value: 'green' },
        { label: 'B', value: 'blue' }, { label: 'A', value: 'alpha' },
      ]}
      fullWidth
      value={channel}
      onChange={(value) => setChannel(value as TextureChannel)}
    />
  </Stack>;
}

function categoryLabel(category: AssetExplorerCategory): string {
  return ({ ties: 'Ties', shrubs: 'Shrubs', mobys: 'Mobys', tfrags: 'Tfrags', skyShells: 'Sky', textures: 'Textures' })[category];
}

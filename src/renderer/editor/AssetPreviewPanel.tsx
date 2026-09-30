import { Badge, Button, Code, Group, SegmentedControl, Select, Stack, Text } from '@mantine/core';
import { useEffect, useRef, useState } from 'react';

import type { AssetExplorerCategory, AssetExplorerItem, AssetPreviewKind } from '../../types/AssetExplorer.js';
import { createAssetPlacementCommand, createSkyShellAddCommand } from '../../utils/AssetPlacement.ts';
import { errorMessage } from '../../utils/Errors.ts';
import { applyTextureChannel, type TextureChannel } from '../../utils/TexturePreview.ts';
import { AssetModelPreview } from './AssetModelPreview.tsx';
import { useEditor } from './EditorContext.ts';
import { EditorEmptyState, EditorPanel } from './EditorPrimitives.tsx';

export function AssetPreviewPanel() {
  const { assetPreview: family, busy, execute } = useEditor();
  const [assetId, setAssetId] = useState('');
  useEffect(() => setAssetId(family?.representativeAssetId ?? ''), [family]);
  if (!family) return <EditorPanel label="Asset Preview">
    <EditorEmptyState message="Select an asset to inspect it." />
  </EditorPanel>;
  const item = family.variants.find((variant) => variant.assetId === assetId)
    ?? family.variants.find((variant) => variant.assetId === family.representativeAssetId)
    ?? family.variants[0];
  const kind = previewKind(item);
  const levels = [...new Set(family.variants.flatMap((variant) => variant.sources.map((source) => source.level)))].sort();
  return <EditorPanel label="Asset Preview">
    <Stack>
      {kind === 'texture' && item.previewState !== 'missingBlob'
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
      <Text size="sm" c="dimmed">Sources: {levels.join(', ')}</Text>
      <Text size="xs" c="dimmed">Asset ID</Text><Code className="asset-explorer-id">{item.assetId}</Code>
      <Text size="sm">
        Canonical format {item.canonicalFormatVersion} · {item.byteSize.toLocaleString()} bytes
      </Text>
      {item.classIds.length > 0 && <Text size="sm">
        Class IDs: {item.classIds.map((value) => `0x${value.toString(16).padStart(4, '0')}`).join(', ')}
      </Text>}
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
      {kind && kind !== 'texture' && kind !== 'sky' && family.classId !== undefined && <Button
        disabled={busy || !item.canPlace}
        title={item.canPlace ? 'Place at project origin' : item.placementDisabledReason}
        onClick={() => void execute(createAssetPlacementCommand({
          assetId: item.assetId, kind, classId: family.classId!,
        }, { x: 0, y: 0, z: 0 }))}
      >Place at origin</Button>}
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

function previewKind(item: AssetExplorerItem): AssetPreviewKind | undefined {
  if (item.category === 'ties') return 'tie';
  if (item.category === 'shrubs') return 'shrub';
  if (item.category === 'mobys') return 'moby';
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
  return ({ ties: 'Ties', shrubs: 'Shrubs', mobys: 'Mobys', skyShells: 'Sky', textures: 'Textures' })[category];
}

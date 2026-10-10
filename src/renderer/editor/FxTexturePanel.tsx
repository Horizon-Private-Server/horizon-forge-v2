import {
  ActionIcon, Alert, Badge, Button, FileButton, Group, Modal, SegmentedControl, Stack, Text, TextInput,
} from '@mantine/core';
import { PencilSimpleIcon } from '@phosphor-icons/react/dist/csr/PencilSimple';
import { useCallback, useEffect, useState } from 'react';

import { errorMessage } from '../../utils/Errors.ts';
import { formatTextureDimensions, prepareTextureImage } from '../../utils/TexturePreview.ts';
import {
  buildFxTextureItems, hudThumbnailDimensions, validateFxPng,
} from '../../utils/TextureInventory.ts';
import { virtualGridWindow } from '../../utils/VirtualGrid.ts';
import type { AssetThumbnailRuntime } from './AssetThumbnailRuntime.ts';
import { useEditor } from './EditorContext.ts';
import { EditorEmptyState } from './EditorPrimitives.tsx';
import {
  useAssetThumbnailRuntime, useTextureCardThumbnail, useVirtualGridViewport,
} from './TexturePanelHooks.ts';

type FxTextureItem = ReturnType<typeof buildFxTextureItems>[number];

interface PendingImage {
  mode: 'replace' | 'append';
  bytes: Uint8Array;
  width: number;
  height: number;
  url: string;
  fileName: string;
  sourceAssetId?: string;
  sourceDimensions?: string;
}

export function FxTexturePanel() {
  const { busy, execute, inspectReferencedAsset, project } = useEditor();
  const fx = project.fx;
  const [search, setSearch] = useState('');
  const [filter, setFilter] = useState<'all' | 'changed' | 'issues'>('all');
  const [error, setError] = useState('');
  const [preparing, setPreparing] = useState(false);
  const [pending, setPending] = useState<PendingImage>();
  const [scrollElement, setScrollElement] = useState<HTMLDivElement | null>(null);
  const thumbnails = useAssetThumbnailRuntime(project.target.game);
  const viewport = useVirtualGridViewport(scrollElement);

  useEffect(() => {
    const url = pending?.url;
    return () => { if (url) URL.revokeObjectURL(url); };
  }, [pending?.url]);

  const prepareImage = useCallback(async (
    file: File | null,
    target?: Pick<FxTextureItem, 'sourceAssetId' | 'width' | 'height'>,
  ) => {
    if (!file) return;
    setPreparing(true);
    setError('');
    try {
      const image = await prepareTextureImage(file, validateFxPng);
      setPending({
        mode: target ? 'replace' : 'append',
        ...image,
        sourceAssetId: target?.sourceAssetId,
        sourceDimensions: target ? `${target.width} × ${target.height}` : undefined,
      });
    } catch (cause) {
      setError(errorMessage(cause));
    } finally {
      setPreparing(false);
    }
  }, []);

  if (!fx?.canRead) return <section aria-label="FX Textures" className="editor-panel">
    <EditorEmptyState message="This project has no readable FX texture inventory." />
  </section>;

  const items = buildFxTextureItems(fx, search, filter);
  const windowed = virtualGridWindow(items.length, viewport.width, viewport.height, viewport.scrollTop, 280, 284);
  const lastAdditionIndex = fx.sourceTextures.length + fx.additions.length - 1;
  const commit = async () => {
    if (!pending) return;
    const committed = pending.mode === 'replace' && pending.sourceAssetId
      ? await execute({
        id: crypto.randomUUID(), kind: 'replaceFxTexture', entityIds: [],
        sourceAssetId: pending.sourceAssetId, imageFormat: 'png', imageBytes: pending.bytes,
      })
      : await execute({
        id: crypto.randomUUID(), kind: 'addFxTexture', entityIds: [],
        imageFormat: 'png', imageBytes: pending.bytes,
      });
    if (committed) setPending(undefined);
  };

  return <section aria-label="FX Textures" className="editor-panel hud-bank-panel">
    <div className="hud-bank-controls">
      <Group justify="space-between" wrap="wrap">
        <Group gap="xs">
          <Text fw={600}>FX Textures</Text>
          <Badge variant="light">{fx.sourceTextures.length + fx.additions.length} / {fx.maximumTextureCount}</Badge>
          {fx.isDirty && <Badge color="yellow" variant="outline">Rebuild required</Badge>}
        </Group>
        <FileButton accept="image/png" disabled={!fx.canAppend || busy || preparing}
          onChange={(file) => void prepareImage(file)}>
          {(props) => <Button {...props} size="xs">Append texture…</Button>}
        </FileButton>
      </Group>
      <div className="hud-bank-filter-row">
        <TextInput aria-label="Search FX textures" placeholder="Search label, index, or Asset ID…"
          value={search} onChange={(event) => setSearch(event.currentTarget.value)} />
        <SegmentedControl aria-label="Filter FX textures" data={[
          { label: 'All', value: 'all' }, { label: 'Changed', value: 'changed' }, { label: 'Issues', value: 'issues' },
        ]} value={filter} onChange={(value) => setFilter(value as typeof filter)} />
      </div>
      {fx.authoringDisabledReason && <Alert color="yellow">{fx.authoringDisabledReason}</Alert>}
      {error && <Alert color="red" title="FX image could not be prepared" withCloseButton
        onClose={() => setError('')}>{error}</Alert>}
    </div>
    <div className="hud-bank-grid-scroll" ref={setScrollElement}>
      <div aria-label="FX texture entries" className="hud-bank-grid" role="list" style={{ height: windowed.totalHeight }}>
        {items.slice(windowed.startIndex, windowed.endIndex).map((item, offset) => {
          const index = windowed.startIndex + offset;
          const row = Math.floor(index / windowed.columns);
          const column = index % windowed.columns;
          return <div key={item.key} className="hud-bank-card-position" role="listitem" style={{
            width: windowed.cardWidth,
            transform: `translate(${column * (windowed.cardWidth + 8)}px, ${row * 284}px)`,
          }}>
            <FxTextureCard item={item} root={scrollElement} runtime={thumbnails} busy={busy || preparing}
              canReplace={fx.canReplace} canRemove={item.kind === 'addition' && item.index === lastAdditionIndex}
              onPrepare={prepareImage}
              onInspect={(assetId) => void inspectReferencedAsset(assetId, 'Texture')}
              onRemove={() => void execute(item.kind === 'addition'
                ? { id: crypto.randomUUID(), kind: 'removeFxTexture', entityIds: [], index: item.index }
                : { id: crypto.randomUUID(), kind: 'removeFxTextureOverride', entityIds: [], sourceAssetId: item.sourceAssetId! })} />
          </div>;
        })}
      </div>
      {!items.length && <Text c="dimmed" className="editor-empty">No FX textures match these filters.</Text>}
    </div>
    <Modal opened={pending !== undefined} onClose={() => setPending(undefined)} title={pending?.mode === 'replace'
      ? 'Validate FX texture replacement' : 'Validate appended FX texture'}>
      {pending && <Stack>
        <div className="hud-image-preview"><img alt={`Preview of ${pending.fileName}`} src={pending.url} /></div>
        <Text size="sm">{pending.fileName} · {pending.width} × {pending.height} · {pending.bytes.byteLength.toLocaleString()} bytes</Text>
        {pending.sourceDimensions && pending.sourceDimensions !== `${pending.width} × ${pending.height}` && <Alert color="yellow">
          Dimensions change from {pending.sourceDimensions} to {pending.width} × {pending.height}.
        </Alert>}
        <Alert color="blue">Forge will quantize this PNG to an indexed 256-color PS2 texture and normalize alpha.</Alert>
        <Group justify="flex-end">
          <Button variant="default" onClick={() => setPending(undefined)}>Cancel</Button>
          <Button disabled={busy} onClick={() => void commit()}>
            {pending.mode === 'replace' ? 'Replace texture' : 'Append texture'}
          </Button>
        </Group>
      </Stack>}
    </Modal>
  </section>;
}

function FxTextureCard({
  item, root, runtime, busy, canReplace, canRemove, onPrepare, onInspect, onRemove,
}: {
  item: FxTextureItem;
  root: HTMLElement | null;
  runtime: AssetThumbnailRuntime | null | undefined;
  busy: boolean;
  canReplace: boolean;
  canRemove: boolean;
  onPrepare(file: File | null, target: Pick<FxTextureItem, 'sourceAssetId' | 'width' | 'height'>): Promise<void>;
  onInspect(assetId: string): void;
  onRemove(): void;
}) {
  const { dimensions, element, failure, onImageLoad, thumbnail } = useTextureCardThumbnail(
    root, runtime, item.previewAssetId,
  );
  const replacement = item.state === 'override' && item.previewAssetId !== item.sourceAssetId;
  const effectiveWidth = dimensions?.width ?? item.width;
  const effectiveHeight = dimensions?.height ?? item.height;
  return <div className="hud-bank-card" data-state={item.state} ref={element}>
    <div className="hud-bank-thumbnail-wrap">
      <button className="hud-bank-thumbnail asset-explorer-thumbnail-texture" disabled={!item.previewAssetId}
        type="button" onClick={() => item.previewAssetId && onInspect(item.previewAssetId)}
        aria-label={`Open FX texture ${item.index} in Asset Preview`}>
        {thumbnail ? <img alt="" onLoad={onImageLoad} src={thumbnail}
          style={hudThumbnailDimensions(effectiveWidth, effectiveHeight)} />
          : <span>{failure || (item.previewAssetId ? 'Preview waiting' : 'Texture missing')}</span>}
      </button>
      {item.kind === 'source' && item.sourceAssetId && <FileButton accept="image/png"
        disabled={!canReplace || busy} onChange={(file) => void onPrepare(file, item)}>
        {(props) => <ActionIcon {...props} aria-label={`Replace FX texture ${item.index}`}
          className="hud-bank-edit" title="Replace texture" variant="filled">
          <PencilSimpleIcon aria-hidden="true" />
        </ActionIcon>}
      </FileButton>}
    </div>
    <Group gap="xs" justify="space-between" wrap="nowrap">
      <Text fw={600}>{item.label}</Text>
      <Group gap={4} wrap="nowrap">
        <Badge color={item.state === 'invalid' ? 'red' : item.state === 'source' ? 'gray' : 'teal'} variant="light">
          {item.state}
        </Badge>
        <Badge variant="outline">#{item.index}</Badge>
      </Group>
    </Group>
    <Text size="xs">{formatTextureDimensions(item.width, item.height, replacement ? dimensions : undefined)} · indexed 8-bit · {item.isSwizzled ? 'swizzled' : 'linear'}</Text>
    {item.paletteOffset !== undefined && <Text size="xs">
      Palette 0x{item.paletteOffset.toString(16).toUpperCase()} · pixels 0x{item.pixelOffset!.toString(16).toUpperCase()}
    </Text>}
    {item.diagnostic && <Text c="red" role="alert" size="xs">{item.diagnostic}</Text>}
    <Group gap="xs" mt="auto">
      {item.sourceAssetId && replacement && <Button size="compact-xs" variant="subtle"
        onClick={() => onInspect(item.sourceAssetId!)}>Source</Button>}
      {(replacement || canRemove) && <Button color="red" disabled={busy} size="compact-xs"
        variant="subtle" onClick={onRemove}>{replacement ? 'Restore source' : 'Remove'}</Button>}
    </Group>
  </div>;
}

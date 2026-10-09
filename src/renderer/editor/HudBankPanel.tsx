import {
  ActionIcon, Alert, Badge, Button, FileButton, Group, Modal, NumberInput, SegmentedControl, Stack, Text,
  TextInput,
} from '@mantine/core';
import { PencilSimpleIcon } from '@phosphor-icons/react/dist/csr/PencilSimple';
import { useCallback, useEffect, useState } from 'react';

import type { EditorHud } from '../../types/EditorRuntime.js';
import { errorMessage } from '../../utils/Errors.ts';
import { formatTextureDimensions, prepareTextureImage } from '../../utils/TexturePreview.ts';
import type { AssetThumbnailRuntime } from './AssetThumbnailRuntime.ts';
import {
  buildHudBankItems, formatHudSpriteId, hudThumbnailDimensions, nextHudSpriteId, validateHudPng,
  virtualGridWindow,
} from './EditorPanelState.ts';
import { useEditor } from './EditorContext.ts';
import { EditorEmptyState } from './EditorPrimitives.tsx';
import {
  useAssetThumbnailRuntime, useTextureCardThumbnail, useVirtualGridViewport,
} from './TexturePanelHooks.ts';

type HudBankItem = ReturnType<typeof buildHudBankItems>[number];

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

export function HudBankPanel() {
  const { busy, execute, inspectReferencedAsset, project } = useEditor();
  const hud = project.hud;
  const [search, setSearch] = useState('');
  const [filter, setFilter] = useState<'all' | 'changed' | 'issues'>('all');
  const [error, setError] = useState('');
  const [preparing, setPreparing] = useState(false);
  const [pending, setPending] = useState<PendingImage>();
  const [spriteIdText, setSpriteIdText] = useState('');
  const [bankIndex, setBankIndex] = useState(hud?.minimumAppendBank ?? 0);
  const [scrollElement, setScrollElement] = useState<HTMLDivElement | null>(null);
  const thumbnails = useAssetThumbnailRuntime(project.target.game);
  const viewport = useVirtualGridViewport(scrollElement);

  useEffect(() => {
    const url = pending?.url;
    return () => { if (url) URL.revokeObjectURL(url); };
  }, [pending?.url]);

  const prepareImage = useCallback(async (
    file: File | null,
    target?: Pick<HudBankItem, 'sourceAssetId' | 'width' | 'height'>,
  ) => {
    if (!file) return;
    setPreparing(true);
    setError('');
    try {
      const image = await prepareTextureImage(file, validateHudPng);
      setPending({
        mode: target ? 'replace' : 'append',
        ...image,
        sourceAssetId: target?.sourceAssetId,
        sourceDimensions: target ? `${target.width} × ${target.height}` : undefined,
      });
      if (!target && hud) {
        const next = nextHudSpriteId(hud, hud.minimumAppendSpriteId, hud.maximumAppendSpriteId);
        setSpriteIdText(next === undefined ? '' : formatHudSpriteId(next));
        setBankIndex(hud.minimumAppendBank);
      }
    } catch (cause) {
      setError(errorMessage(cause));
    } finally {
      setPreparing(false);
    }
  }, [hud]);

  if (!hud?.canRead) return <section aria-label="HUD Bank" className="editor-panel">
    <EditorEmptyState message="This project has no readable HUD inventory." />
  </section>;

  const items = buildHudBankItems(hud, search, filter);
  const windowed = virtualGridWindow(items.length, viewport.width, viewport.height, viewport.scrollTop, 280, 284);
  const occupied = new Set([
    ...hud.sourceIcons.map((icon) => icon.spriteId), ...hud.additions.map((addition) => addition.spriteId),
  ]);
  const parsedSpriteId = /^[0-9a-f]{4}$/i.test(spriteIdText.trim())
    ? Number.parseInt(spriteIdText.trim(), 16) : undefined;
  const spriteError = pending?.mode === 'append' && (parsedSpriteId === undefined
    ? 'Enter exactly four hexadecimal digits.'
    : parsedSpriteId < hud.minimumAppendSpriteId || parsedSpriteId > hud.maximumAppendSpriteId
      ? `Use ${formatHudSpriteId(hud.minimumAppendSpriteId)}–${formatHudSpriteId(hud.maximumAppendSpriteId)}.`
      : occupied.has(parsedSpriteId) ? 'That sprite ID is already in use.' : undefined);
  const bankError = pending?.mode === 'append'
    && (bankIndex < hud.minimumAppendBank || bankIndex >= hud.physicalBankCount)
    ? `Use bank ${hud.minimumAppendBank} through ${hud.physicalBankCount - 1}.` : undefined;

  const commit = async () => {
    if (!pending) return;
    const committed = pending.mode === 'replace' && pending.sourceAssetId
      ? await execute({
        id: crypto.randomUUID(), kind: 'replaceHudTexture', entityIds: [],
        sourceAssetId: pending.sourceAssetId, imageFormat: 'png', imageBytes: pending.bytes,
      })
      : parsedSpriteId !== undefined && !spriteError && !bankError
        ? await execute({
          id: crypto.randomUUID(), kind: 'addHudIcon', entityIds: [], spriteId: parsedSpriteId,
          bankIndex, imageFormat: 'png', imageBytes: pending.bytes,
        })
        : false;
    if (committed) setPending(undefined);
  };

  return <section aria-label="HUD Bank" className="editor-panel hud-bank-panel">
    <div className="hud-bank-controls">
      <Group justify="space-between" wrap="wrap">
        <Group gap="xs">
          <Text fw={600}>HUD Bank</Text>
          <Badge variant="light">{hud.sourceIcons.length + hud.additions.length} / {hud.maximumIconCount} icons</Badge>
          <Badge variant="outline">{hud.physicalBankCount} physical banks</Badge>
        </Group>
        <FileButton accept="image/png" disabled={!hud.canAppend || busy || preparing}
          onChange={(file) => void prepareImage(file)}>
          {(props) => <Button {...props} size="xs">Append icon…</Button>}
        </FileButton>
      </Group>
      <div className="hud-bank-filter-row">
        <TextInput aria-label="Search HUD bank" placeholder="Search sprite ID, bank, index, or Asset ID…"
          value={search} onChange={(event) => setSearch(event.currentTarget.value)} />
        <SegmentedControl aria-label="Filter HUD entries" data={[
          { label: 'All', value: 'all' }, { label: 'Changed', value: 'changed' }, { label: 'Issues', value: 'issues' },
        ]} value={filter} onChange={(value) => setFilter(value as typeof filter)} />
      </div>
      {hud.authoringDisabledReason && <Alert color="yellow">{hud.authoringDisabledReason}</Alert>}
      {error && <Alert color="red" title="HUD image could not be prepared" withCloseButton
        onClose={() => setError('')}>{error}</Alert>}
    </div>
    <div className="hud-bank-grid-scroll" ref={setScrollElement}>
      <div aria-label="HUD entries" className="hud-bank-grid" role="list" style={{ height: windowed.totalHeight }}>
        {items.slice(windowed.startIndex, windowed.endIndex).map((item, offset) => {
          const index = windowed.startIndex + offset;
          const row = Math.floor(index / windowed.columns);
          const column = index % windowed.columns;
          return <div key={item.key} className="hud-bank-card-position" role="listitem" style={{
            width: windowed.cardWidth,
            transform: `translate(${column * (windowed.cardWidth + 8)}px, ${row * 284}px)`,
          }}>
            <HudBankCard item={item} root={scrollElement} runtime={thumbnails} busy={busy || preparing}
              canReplace={hud.canReplace} onPrepare={prepareImage}
              onInspect={(assetId) => void inspectReferencedAsset(assetId, 'Texture')}
              onRemove={() => void execute(item.kind === 'addition'
                ? { id: crypto.randomUUID(), kind: 'removeHudIcon', entityIds: [], spriteId: item.spriteId }
                : { id: crypto.randomUUID(), kind: 'removeHudTextureOverride', entityIds: [], sourceAssetId: item.sourceAssetId! })} />
          </div>;
        })}
      </div>
      {!items.length && <Text c="dimmed" className="editor-empty">No HUD entries match these filters.</Text>}
    </div>
    <Modal opened={pending !== undefined} onClose={() => setPending(undefined)} title={pending?.mode === 'replace'
      ? 'Validate HUD texture replacement' : 'Validate appended HUD icon'}>
      {pending && <Stack>
        <div className="hud-image-preview"><img alt={`Preview of ${pending.fileName}`} src={pending.url} /></div>
        <Text size="sm">{pending.fileName} · {pending.width} × {pending.height} · {pending.bytes.byteLength.toLocaleString()} bytes</Text>
        {pending.sourceDimensions && pending.sourceDimensions !== `${pending.width} × ${pending.height}` && <Alert color="yellow">
          Dimensions change from {pending.sourceDimensions} to {pending.width} × {pending.height}.
        </Alert>}
        <Alert color="blue">Forge will quantize this PNG to an indexed 256-color PS2 texture and normalize alpha.</Alert>
        {pending.mode === 'append' && <>
          <TextInput aria-label="Sprite ID" label="Sprite ID (four hexadecimal digits)" value={spriteIdText}
            error={spriteError} onChange={(event) => setSpriteIdText(event.currentTarget.value.toUpperCase())} />
          <NumberInput aria-label="Physical HUD bank" label="Physical bank" min={hud.minimumAppendBank}
            max={hud.physicalBankCount - 1} error={bankError}
            value={bankIndex} onChange={(value) => setBankIndex(Number(value))} />
        </>}
        <Group justify="flex-end">
          <Button variant="default" onClick={() => setPending(undefined)}>Cancel</Button>
          <Button disabled={busy || Boolean(spriteError) || Boolean(bankError)} onClick={() => void commit()}>
            {pending.mode === 'replace' ? 'Replace texture' : 'Append icon'}
          </Button>
        </Group>
      </Stack>}
    </Modal>
  </section>;
}

function HudBankCard({ item, root, runtime, busy, canReplace, onPrepare, onInspect, onRemove }: {
  item: HudBankItem;
  root: HTMLElement | null;
  runtime: AssetThumbnailRuntime | null | undefined;
  busy: boolean;
  canReplace: boolean;
  onPrepare(file: File | null, target: Pick<HudBankItem, 'sourceAssetId' | 'width' | 'height'>): Promise<void>;
  onInspect(assetId: string): void;
  onRemove(): void;
}) {
  const { dimensions, element, failure, onImageLoad, thumbnail } = useTextureCardThumbnail(
    root, runtime, item.previewAssetId,
  );
  const replacement = item.state === 'override' && item.previewAssetId !== item.sourceAssetId;
  const effectiveWidth = dimensions?.width ?? item.width;
  const effectiveHeight = dimensions?.height ?? item.height;
  const thumbnailSize = hudThumbnailDimensions(effectiveWidth, effectiveHeight);
  return <div className="hud-bank-card" data-state={item.state} ref={element}>
    <div className="hud-bank-thumbnail-wrap">
      <button className="hud-bank-thumbnail asset-explorer-thumbnail-texture" disabled={!item.previewAssetId}
        type="button" onClick={() => item.previewAssetId && onInspect(item.previewAssetId)}
        aria-label={`Open ${replacement ? 'replacement' : item.kind === 'addition' ? 'new' : 'source'} texture for sprite ${formatHudSpriteId(item.spriteId)} in Asset Preview`}>
        {thumbnail ? <img alt="" onLoad={onImageLoad} src={thumbnail} style={thumbnailSize} />
          : <span>{failure || (item.previewAssetId ? 'Preview waiting' : 'Texture missing')}</span>}
      </button>
      {item.kind === 'frame' && item.sourceAssetId && <FileButton accept="image/png"
        disabled={!canReplace || busy} onChange={(file) => void onPrepare(file, item)}>
        {(props) => <ActionIcon {...props} aria-label={`Replace texture for sprite ${formatHudSpriteId(item.spriteId)}`}
          className="hud-bank-edit" title="Replace texture" variant="filled">
          <PencilSimpleIcon aria-hidden="true" />
        </ActionIcon>}
      </FileButton>}
    </div>
    <Group gap="xs" justify="space-between" wrap="nowrap">
      <Text fw={600}>{formatHudSpriteId(item.spriteId)}</Text>
      <Group gap={4} wrap="nowrap">
        <Badge color={item.state === 'invalid' ? 'red' : item.state === 'source' ? 'gray' : 'teal'} variant="light">
          {item.state}
        </Badge>
        <Badge variant="outline">{item.kind === 'addition' ? 'icon' : 'frame'}</Badge>
      </Group>
    </Group>
    <Text size="xs">{formatTextureDimensions(item.width, item.height, replacement ? dimensions : undefined)} · palette {item.sourcePaletteIndex ?? 'new'} · texture {item.sourceTextureIndex ?? 'new'}</Text>
    <Text size="xs">Banks: palette {item.paletteBankIndex} · texture {item.textureBankIndex}</Text>
    {item.sourceFrameIndex !== undefined && <Text size="xs">Icon {item.sourceIconIndex} · frame {item.sourceFrameIndex}</Text>}
    {item.diagnostic && <Text c="red" role="alert" size="xs">{item.diagnostic}</Text>}
    <Group gap="xs" mt="auto">
      {item.sourceAssetId && replacement && <Button size="compact-xs" variant="subtle"
        onClick={() => onInspect(item.sourceAssetId!)}>Source</Button>}
      {(replacement || item.kind === 'addition') && <Button color="red" disabled={busy} size="compact-xs"
        variant="subtle" onClick={onRemove}>{replacement ? 'Restore source' : 'Remove'}</Button>}
    </Group>
  </div>;
}

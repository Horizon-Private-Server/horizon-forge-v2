import { Badge, Code, Group, Select, Stack, Text } from '@mantine/core';
import { useEffect, useState } from 'react';

import type { AssetExplorerCategory, AssetExplorerItem, AssetPreviewKind } from '../../types/AssetExplorer.js';
import { AssetModelPreview } from './AssetModelPreview.tsx';
import { useEditor } from './EditorContext.ts';
import { EditorEmptyState, EditorPanel } from './EditorPrimitives.tsx';

export function AssetPreviewPanel() {
  const { assetPreview: family } = useEditor();
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
      {kind && item.previewState !== 'missingBlob'
        ? <AssetModelPreview assetId={item.assetId} kind={kind} label={item.displayLabel} />
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
      {item.aliases.length > 0 && <Text size="sm">Aliases: {item.aliases.join(', ')}</Text>}
      {item.tags.length > 0 && <Group gap="xs">{item.tags.map((tag) => <Badge key={tag}>{tag}</Badge>)}</Group>}
      {item.sources.map((source) => <Text
        key={`${source.game}:${source.region}:${source.revision}:${source.level}:${source.archive}:${source.sourceIndex}`}
        size="sm"
      >
        {source.game} · {source.region} · {source.revision} · Level {source.level}
      </Text>)}
      {!item.canPlace && <Text c="dimmed" size="sm">{item.placementDisabledReason}</Text>}
    </Stack>
  </EditorPanel>;
}

function previewKind(item: AssetExplorerItem): AssetPreviewKind | undefined {
  if (item.category === 'ties') return 'tie';
  if (item.category === 'shrubs') return 'shrub';
  if (item.category === 'mobys') return 'moby';
  return undefined;
}

function categoryLabel(category: AssetExplorerCategory): string {
  return ({ ties: 'Ties', shrubs: 'Shrubs', mobys: 'Mobys', skyShells: 'Sky', textures: 'Textures' })[category];
}

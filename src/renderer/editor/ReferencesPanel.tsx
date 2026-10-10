import { Badge, Button, Group, Stack, Tabs, Text, UnstyledButton } from '@mantine/core';
import { useEffect, useMemo, useState } from 'react';

import type { EditorEntity, EditorReference } from '../../types/EditorRuntime.js';
import { buildReferenceIndex, referenceNavigationTarget, referencePage } from '../../utils/EditorReferences.ts';
import { useEditor } from './EditorContext.ts';
import { EditorEmptyState, EditorFilter, EditorPanel } from './EditorPrimitives.tsx';

type Direction = 'incoming' | 'outgoing';

export function ReferencesPanel() {
  const { project } = useEditor();
  const selectedId = project.selection.length === 1 ? project.selection[0] : undefined;
  const [model, setModel] = useState(() => ({
    index: buildReferenceIndex([]),
    entities: new Map<string, EditorEntity>(),
  }));
  useEffect(() => setModel({
    index: buildReferenceIndex(project.references),
    entities: new Map(project.entities.map((entity) => [entity.id, entity])),
  }), [project.entities, project.references]);
  return <EditorPanel label="References">
    {!selectedId
      ? <EditorEmptyState message="Select one object to inspect its references." />
      : <ReferenceTabs selectedId={selectedId} index={model.index} entities={model.entities} />}
  </EditorPanel>;
}

function ReferenceTabs({ selectedId, index, entities }: {
  selectedId: string;
  index: ReturnType<typeof buildReferenceIndex>;
  entities: ReadonlyMap<string, EditorEntity>;
}) {
  const [filter, setFilter] = useState('');
  const [pages, setPages] = useState<Record<Direction, number>>({ incoming: 0, outgoing: 0 });
  useEffect(() => setPages({ incoming: 0, outgoing: 0 }), [filter, selectedId]);
  return <Stack>
    <Text c="dimmed" size="xs">
      Partial coverage: only understood Forge references are shown; opaque fields may contain additional links.
    </Text>
    <EditorFilter label="Filter references" value={filter} onChange={setFilter} />
    <Tabs defaultValue="outgoing">
      <Tabs.List grow>
        <Tabs.Tab value="outgoing">Outgoing ({index.outgoing.get(selectedId)?.length ?? 0})</Tabs.Tab>
        <Tabs.Tab value="incoming">Incoming ({index.incoming.get(selectedId)?.length ?? 0})</Tabs.Tab>
      </Tabs.List>
      {(['outgoing', 'incoming'] as const).map((direction) => <Tabs.Panel key={direction} value={direction} pt="xs">
        <ReferenceList
          direction={direction}
          entities={entities}
          filter={filter}
          page={pages[direction]}
          references={index[direction].get(selectedId) ?? []}
          onPageChange={(page) => setPages((current) => ({ ...current, [direction]: page }))}
        />
      </Tabs.Panel>)}
    </Tabs>
  </Stack>;
}

function ReferenceList({ direction, entities, filter, page, references, onPageChange }: {
  direction: Direction;
  entities: ReadonlyMap<string, EditorEntity>;
  filter: string;
  page: number;
  references: readonly EditorReference[];
  onPageChange(page: number): void;
}) {
  const query = filter.trim().toLocaleLowerCase();
  const filtered = useMemo(() => references.filter((reference) => {
    const owner = entities.get(reference.ownerEntityId);
    const targetEntity = reference.domain === 'entity' && reference.targetEntityId
      ? entities.get(reference.targetEntityId) : undefined;
    const target = reference.domain === 'asset'
      ? `${reference.targetKind} ${reference.targetAssetId ?? ''}`
      : `${reference.targetKind} ${reference.targetEntityId ?? ''}`;
    return !query || [reference.fieldKey, reference.sourceValue, reference.datasetSource,
      owner?.name, targetEntity?.name, target].join(' ').toLocaleLowerCase().includes(query);
  }), [direction, entities, query, references]);
  const result = referencePage(filtered, page);
  if (!result.values.length) return <EditorEmptyState message={`No ${direction} understood references.`} />;
  return <Stack gap="xs">
    <Stack component="ul" className="reference-list" gap={4}>
      {result.values.map((reference) => <ReferenceRow
        key={`${reference.ownerEntityId}:${reference.fieldKey}:${reference.sourceValue ?? ''}`}
        direction={direction}
        entities={entities}
        reference={reference}
      />)}
    </Stack>
    {result.pageCount > 1 && <Group justify="space-between">
      <Button disabled={result.page === 0} variant="default" onClick={() => onPageChange(result.page - 1)}>Previous</Button>
      <Text size="xs">Page {result.page + 1} of {result.pageCount}</Text>
      <Button disabled={result.page + 1 === result.pageCount} variant="default"
        onClick={() => onPageChange(result.page + 1)}>Next</Button>
    </Group>}
  </Stack>;
}

function ReferenceRow({ direction, entities, reference }: {
  direction: Direction;
  entities: ReadonlyMap<string, EditorEntity>;
  reference: EditorReference;
}) {
  const { inspectReferencedAsset, navigateToEntity } = useEditor();
  const owner = entities.get(reference.ownerEntityId);
  const targetEntityId = reference.domain === 'entity' ? reference.targetEntityId : undefined;
  const targetEntity = targetEntityId ? entities.get(targetEntityId) : undefined;
  const targetAssetId = reference.domain === 'asset' ? reference.targetAssetId : undefined;
  const navigation = referenceNavigationTarget(reference, direction);
  const navigationEntityId = navigation.domain === 'entity' ? navigation.id : undefined;
  const navigationAssetId = navigation.domain === 'asset' ? navigation.id : undefined;
  const missing = reference.missing || Boolean(targetEntityId && !targetEntity);
  const nullTarget = !missing && !targetEntityId && !targetAssetId;
  const targetLabel = targetEntity?.name
    ?? (targetAssetId ? `${reference.targetKind} asset`
      : nullTarget ? 'None'
        : `Missing ${reference.targetKind}`);
  const activate = () => navigationEntityId
    ? void navigateToEntity(navigationEntityId, 'reveal')
    : navigationAssetId && void inspectReferencedAsset(navigationAssetId, navigation.kind ?? '');
  return <li className="reference-row">
    <UnstyledButton
      aria-label={`${direction} ${reference.fieldKey} reference`}
      className="reference-row-target"
      disabled={!navigationEntityId && !navigationAssetId}
      onClick={(event) => event.detail === 1 && activate()}
      onDoubleClick={() => navigationEntityId && !missing && void navigateToEntity(navigationEntityId, 'focus')}
    >
      <Text fw={500} size="sm">{owner?.name ?? 'Missing owner'} → {targetLabel}</Text>
      <Text c="dimmed" size="xs">{reference.fieldKey}{reference.sourceValue === undefined
        ? '' : ` · encoded ${reference.sourceValue}`}</Text>
      {reference.datasetSource && <Text c="dimmed" size="xs">{reference.datasetSource}</Text>}
      <Group gap={4} mt={4}>
        <Badge color={missing ? 'red' : 'gray'} variant="light">
          {missing ? 'Missing' : nullTarget ? 'None' : reference.domain}
        </Badge>
        <Badge variant="light">{reference.targetKind}</Badge>
        {reference.datasetSource && <Badge color="blue" variant="light">MobyDex</Badge>}
      </Group>
    </UnstyledButton>
    <Group className="reference-row-actions" gap={4}>
      <Button disabled={!owner} variant="default"
        onClick={() => void navigateToEntity(reference.ownerEntityId, 'select')}>Owner</Button>
      {reference.domain === 'entity' && <Button disabled={missing || !targetEntityId} variant="default"
        onClick={() => targetEntityId && void navigateToEntity(targetEntityId, 'select')}>Target</Button>}
      {targetAssetId && <Button disabled={missing} variant="default"
        onClick={() => void inspectReferencedAsset(targetAssetId, reference.targetKind)}>Open preview</Button>}
    </Group>
  </li>;
}

import { Alert, Badge, Button, Code, Group, Modal, Paper, ScrollArea, Stack, Text } from '@mantine/core';
import { useEffect, useMemo, useState } from 'react';

import type { ContentImportRemovalPlan } from '../../types/ContentImport.js';
import type { EditorEntity } from '../../types/EditorRuntime.js';
import { errorMessage } from '../../utils/Errors.ts';
import { useEditor } from './EditorContext.ts';
import { EditorEmptyState, EditorFilter, EditorPanel } from './EditorPrimitives.tsx';

export function ImportBatchesPanel() {
  const {
    project, busy, execute, navigateToEntity, activeImportBatchId, openImportBatch, filterSceneToImportBatch,
    removeImportBatch,
  } = useEditor();
  const [filter, setFilter] = useState('');
  const [removalPlan, setRemovalPlan] = useState<ContentImportRemovalPlan>();
  const [removalLoading, setRemovalLoading] = useState<string>();
  const [removalError, setRemovalError] = useState('');
  useEffect(() => {
    if (activeImportBatchId) setFilter(activeImportBatchId);
  }, [activeImportBatchId]);
  const liveEntities = useMemo(() => groupEntitiesByBatch(project.entities), [project.entities]);
  const liveGroups = useMemo(() => {
    const counts = new Map<string, number>();
    for (const group of project.groups) {
      const batchId = group.origin?.importBatchId;
      if (batchId) counts.set(batchId, (counts.get(batchId) ?? 0) + 1);
    }
    return counts;
  }, [project.groups]);
  const batches = useMemo(() => {
    const query = filter.trim().toLocaleLowerCase();
    return [...project.importBatches].reverse().filter((batch) => !query || [
      batch.id,
      batch.sourceGame,
      batch.sourceRegion,
      batch.sourceRevision,
      `level ${batch.sourceLevel}`,
      `l${batch.sourceLevel}`,
      ...batch.families.flatMap((family) => [family.family, family.mode]),
    ].join(' ').toLocaleLowerCase().includes(query));
  }, [filter, project.importBatches]);
  const previewRemoval = async (batchId: string) => {
    setRemovalLoading(batchId);
    setRemovalError('');
    try { setRemovalPlan(await window.forge.planContentImportRemoval(batchId)); }
    catch (cause) { setRemovalError(errorMessage(cause)); }
    finally { setRemovalLoading(undefined); }
  };

  return <EditorPanel label="Imports">
    <Stack gap="xs" p="xs" h="100%">
      <EditorFilter label="Filter import batches" value={filter} onChange={setFilter} />
      {removalError && <Alert color="red" withCloseButton onClose={() => setRemovalError('')}>
        {removalError}
      </Alert>}
      {!project.importBatches.length
        ? <EditorEmptyState message="This project has no map content imports." />
        : !batches.length
          ? <EditorEmptyState message="No import batches match the filter." />
          : <ScrollArea flex={1} offsetScrollbars>
            <Stack gap="xs">
              {batches.slice(0, 50).map((batch) => {
                const entities = liveEntities.get(batch.id) ?? [];
                const groupCount = liveGroups.get(batch.id) ?? 0;
                return <Paper key={batch.id} withBorder p="sm">
                  <Stack gap="xs">
                    <Group justify="space-between" align="start" wrap="nowrap">
                      <Stack gap={2} miw={0}>
                        <Button
                          aria-label={`Open import batch ${batch.id}`}
                          justify="start"
                          p={0}
                          size="compact-sm"
                          variant="transparent"
                          onClick={() => openImportBatch(batch.id)}
                        >
                          <Code>{batch.id}</Code>
                        </Button>
                        <Text size="sm">
                          {batch.sourceGame} {batch.sourceRegion} {batch.sourceRevision} · level {batch.sourceLevel}
                        </Text>
                        <Text c="dimmed" size="xs">{new Date(batch.importedUnixMilliseconds).toLocaleString()}</Text>
                      </Stack>
                      <Text c="dimmed" size="xs" ta="right">
                        {entities.length} live {entities.length === 1 ? 'entity' : 'entities'} · {groupCount} live {
                          groupCount === 1 ? 'group' : 'groups'}
                      </Text>
                    </Group>
                    <Group gap={4}>
                      {batch.families.map((family) => <Badge key={`${family.family}:${family.mode}`} variant="light">
                        {familyLabel(family.family)} · {family.mode}
                      </Badge>)}
                    </Group>
                    <Text c="dimmed" size="xs">
                      Added {batch.addedCount.toLocaleString()} · reused {batch.reusedCount.toLocaleString()} · replaced {
                        batch.replacedCount.toLocaleString()} · protected {batch.protectedCount.toLocaleString()}
                    </Text>
                    <Group gap="xs">
                      <Button size="compact-xs" variant="default" onClick={() => filterSceneToImportBatch(batch.id)}>
                        Filter Scene
                      </Button>
                      <Button
                        disabled={busy || !entities.length}
                        size="compact-xs"
                        variant="default"
                        onClick={() => void execute({
                          id: crypto.randomUUID(), kind: 'setSelection', entityIds: entities.map((entity) => entity.id),
                        })}
                      >
                        Select current
                      </Button>
                      <Button
                        color="red"
                        disabled={busy}
                        loading={removalLoading === batch.id}
                        size="compact-xs"
                        variant="light"
                        onClick={() => void previewRemoval(batch.id)}
                      >
                        Remove import…
                      </Button>
                    </Group>
                    {entities.length > 0 && <Group gap={4}>
                      {entities.slice(0, 20).map((entity) => <Button
                        key={entity.id}
                        size="compact-xs"
                        variant="subtle"
                        onClick={() => void navigateToEntity(entity.id, 'focus')}
                      >
                        {entity.name}
                      </Button>)}
                      {entities.length > 20 && <Text c="dimmed" size="xs">+{entities.length - 20} more</Text>}
                    </Group>}
                  </Stack>
                </Paper>;
              })}
              {batches.length > 50 && <Text c="dimmed" size="xs">
                Showing 50 of {batches.length} batches. Refine the filter to find older imports.
              </Text>}
            </Stack>
          </ScrollArea>}
    </Stack>
    <Modal
      opened={removalPlan !== undefined}
      size="lg"
      title="Remove imported content"
      onClose={() => { if (!busy) setRemovalPlan(undefined); }}
    >
      {removalPlan && <Stack gap="sm">
        <Text size="sm"><Code>{removalPlan.batchId}</Code></Text>
        <Text size="sm">
          {removalPlan.delete.length.toLocaleString()} items will be removed. {
            removalPlan.survivors.length.toLocaleString()} batch-owned items will survive; {
            removalPlan.shared.length.toLocaleString()} reused and {removalPlan.protected.length.toLocaleString()} protected items are unchanged.
        </Text>
        {removalPlan.warnings.map((warning) => <Alert color="yellow" key={warning}>{warning}</Alert>)}
        <RemovalItems label="Remove" values={removalPlan.delete} />
        <RemovalItems
          label="Retain"
          values={removalPlan.survivors.map((value) => `${value.stableId} — ${value.reason}`)}
        />
        <RemovalItems label="Shared" values={removalPlan.shared} />
        <RemovalItems label="Protected" values={removalPlan.protected} />
        {!removalPlan.delete.length && <Alert color="blue">
          This batch has no dependency-safe content to remove.
        </Alert>}
        <Group justify="flex-end">
          <Button disabled={busy} variant="default" onClick={() => setRemovalPlan(undefined)}>Cancel</Button>
          <Button
            color="red"
            disabled={busy || !removalPlan.delete.length}
            loading={busy}
            onClick={() => void removeImportBatch(
              removalPlan.batchId, removalPlan.targetFingerprint,
            ).then((removed) => { if (removed) setRemovalPlan(undefined); })}
          >
            Remove {removalPlan.delete.length.toLocaleString()} items
          </Button>
        </Group>
      </Stack>}
    </Modal>
  </EditorPanel>;
}

function RemovalItems({ label, values }: { label: string; values: readonly string[] }) {
  if (!values.length) return null;
  return <Stack gap={2}>
    <Text fw={600} size="sm">{label} ({values.length.toLocaleString()})</Text>
    {values.slice(0, 50).map((value) => <Code block key={value}>{value}</Code>)}
    {values.length > 50 && <Text c="dimmed" size="xs">+{values.length - 50} more</Text>}
  </Stack>;
}

function groupEntitiesByBatch(entities: readonly EditorEntity[]): Map<string, EditorEntity[]> {
  const groups = new Map<string, EditorEntity[]>();
  for (const entity of entities) {
    const batchId = entity.origin?.importBatchId;
    if (!batchId) continue;
    const values = groups.get(batchId);
    if (values) values.push(entity);
    else groups.set(batchId, [entity]);
  }
  return groups;
}

function familyLabel(value: string): string {
  return value.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, (letter) => letter.toUpperCase());
}

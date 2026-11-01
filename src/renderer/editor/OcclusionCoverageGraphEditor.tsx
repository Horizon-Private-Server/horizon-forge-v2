import { ActionIcon, Fieldset, Group, Pagination, Stack, Text } from '@mantine/core';
import { PlusIcon } from '@phosphor-icons/react/dist/csr/Plus';
import { TrashIcon } from '@phosphor-icons/react/dist/csr/Trash';
import { useEffect, useState } from 'react';
import type { OcclusionCoverageAuthoring, OcclusionCoverageMarker, OcclusionCoverageSource } from '../../types/OcclusionCoverage.js';
import { clampOcclusionSeed, createOcclusionCoverageMarker, occlusionSeedLabel, OCCLUSION_COVERAGE_LIMITS } from '../../utils/OcclusionCoverage.ts';
import { projectPositionToLocal } from '../../utils/Transforms.ts';
import { EditorTree } from './EditorPrimitives.tsx';

const PAGE_SIZE = 32;

export function OcclusionCoverageGraphEditor({ source, authoring, disabled }: {
  source: OcclusionCoverageSource; authoring: OcclusionCoverageAuthoring; disabled: boolean;
}) {
  const markers = source.graph!.markers;
  const selected = markers.find(marker => marker.id === authoring.selectedMarkerId);
  const [page, setPage] = useState(1);
  const total = Math.max(1, Math.ceil(markers.length / PAGE_SIZE));
  const currentPage = Math.min(page, total);
  useEffect(() => {
    const index = markers.findIndex(marker => marker.id === authoring.selectedMarkerId);
    if (index >= 0) setPage(Math.floor(index / PAGE_SIZE) + 1);
  }, [authoring.selectedMarkerId, markers]);
  const state = authoring.snapshot!.state;
  const canAdd = markers.length < OCCLUSION_COVERAGE_LIMITS.markersPerGraph
    && state.sources.reduce((count, s) => count + (s.graph?.markers.length ?? 0), 0) < OCCLUSION_COVERAGE_LIMITS.markers;
  const apply = async (values: OcclusionCoverageMarker[], selectedId?: string) => {
    const updated = { ...source, graph: { ...source.graph!, markers: values } };
    if (await authoring.commit({ ...state, sources: state.sources.map(s => s.id === source.id ? updated : s) })) {
      authoring.select(source.id, selectedId);
    }
  };
  const add = () => {
    const ahead = authoring.placementPosition();
    const local = ahead ? projectPositionToLocal(source.transform, ahead) : { x: 0, y: 0, z: 0 };
    const position = clampOcclusionSeed(source, local);
    const marker = createOcclusionCoverageMarker(occlusionSeedLabel(markers.length), position);
    void apply([...markers, marker], marker.id);
  };
  return <Stack gap="sm" aria-label={`Graph ${source.name}`}>
    <Fieldset legend={`Seeds (${markers.length.toLocaleString()})`}>
      <Stack gap="xs">
        {source.graph!.disabled && <Text size="xs" role="status">Seed filtering is off. This volume fills its shape; its seeds are retained.</Text>}
        {!source.enabled && <Text size="xs" role="status">Graph disabled. Add seeds, then check “Use when generating octants” in Volume properties and apply changes.</Text>}
        <div className="occlusion-seed-list">
          {!markers.length ? <Text size="xs" c="dimmed" p="sm" role="status">No seeds yet. Use + below to add one.</Text>
            : <EditorTree label={`Seeds in ${source.name}`} selected={selected ? [selected.id] : []}
              nodes={markers.slice((currentPage - 1) * PAGE_SIZE, currentPage * PAGE_SIZE).map((marker, index) => {
                const label = occlusionSeedLabel((currentPage - 1) * PAGE_SIZE + index);
                return {
                  value: marker.id,
                  label: <span className="scene-tree-node">
                    <span className="occlusion-seed-name">{label}</span>
                    {selected?.id === marker.id && <ActionIcon size="sm" color="red" variant="subtle" disabled={disabled}
                      aria-label={`Delete ${label}`} title={`Delete ${label}`}
                      onClick={event => { event.stopPropagation(); void apply(markers.filter(m => m.id !== marker.id)); }}>
                      <TrashIcon size={14} />
                    </ActionIcon>}
                  </span>,
                };
              })}
              onSelectionChange={values => authoring.select(source.id, values[0])}
              onActivate={id => authoring.focus(source.id, id)} />}
          <Group justify="center" p={4}>
            <ActionIcon size="sm" variant="subtle" disabled={disabled || !canAdd} aria-label="Add seed" title="Add seed" onClick={add}>
              <PlusIcon size={16} />
            </ActionIcon>
          </Group>
        </div>
        {total > 1 && <Pagination size="xs" value={currentPage} onChange={setPage} total={total} aria-label="Graph seed pages" />}
        <Text size="xs" c="dimmed">New seeds appear ahead of the camera within the volume. Select a seed and use Move to position it; double-click to focus. Copy, paste and delete use the selected seed.</Text>
      </Stack>
    </Fieldset>
  </Stack>;
}

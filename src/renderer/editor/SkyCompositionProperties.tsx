import { CaretLeftIcon } from '@phosphor-icons/react/dist/csr/CaretLeft';
import { CaretRightIcon } from '@phosphor-icons/react/dist/csr/CaretRight';
import { TrashIcon } from '@phosphor-icons/react/dist/csr/Trash';
import { ActionIcon, Group, Stack, Text } from '@mantine/core';
import { useEffect, useState } from 'react';

import type { EditorEntity } from '../../types/EditorRuntime.js';
import { AssetThumbnailRuntime } from './AssetThumbnailRuntime.ts';
import { useEditor } from './EditorContext.ts';
import { EditorEmptyState } from './EditorPrimitives.tsx';

const SKY_SHELL_MIME = 'application/x-horizon-forge-sky-shell';

export function SkyCompositionProperties() {
  const { project, execute, busy, setSkyCompositionSelected } = useEditor();
  const shells = project.entities.filter((entity) => entity.skyShell)
    .sort((left, right) => left.skyShell!.order - right.skyShell!.order);
  const [runtime, setRuntime] = useState<AssetThumbnailRuntime>();
  const [dropTarget, setDropTarget] = useState<string>();

  useEffect(() => {
    let next: AssetThumbnailRuntime | undefined;
    try { next = new AssetThumbnailRuntime(project.target.game); }
    catch { return; }
    setRuntime(next);
    return () => next?.dispose();
  }, [project.target.game]);

  const reorder = (entity: EditorEntity, destinationOrder: number) => {
    if (busy || entity.state.locked || entity.state.readOnly || entity.skyShell!.order === destinationOrder) return;
    void execute({
      id: crypto.randomUUID(), kind: 'reorderSkyShell', entityIds: [entity.id], destinationOrder,
    });
  };

  if (!shells.length) return <EditorEmptyState message="The project sky has no shells. Add one from Asset Explorer." />;

  return <Stack gap="xs">
    <Group justify="space-between">
      <Text fw={600}>Sky composition</Text>
      <Text c="dimmed" size="xs">{shells.length} {shells.length === 1 ? 'shell' : 'shells'}</Text>
    </Group>
    <Text c="dimmed" size="xs">Drag shells to change their draw order. Select one to edit its properties.</Text>
    <div className="sky-composition-strip" role="list" aria-label="Sky shell draw order">
      {shells.map((entity, index) => {
        const disabled = busy || entity.state.locked || entity.state.readOnly;
        return <div
          className="sky-composition-card"
          data-drop-target={dropTarget === entity.id || undefined}
          draggable={!disabled}
          key={entity.id}
          role="listitem"
          onDragStart={(event) => {
            event.dataTransfer.effectAllowed = 'move';
            event.dataTransfer.setData(SKY_SHELL_MIME, entity.id);
          }}
          onDragEnd={() => setDropTarget(undefined)}
          onDragOver={(event) => {
            if (event.dataTransfer.types.includes(SKY_SHELL_MIME)) {
              event.preventDefault();
              event.dataTransfer.dropEffect = 'move';
              setDropTarget(entity.id);
            }
          }}
          onDrop={(event) => {
            event.preventDefault();
            setDropTarget(undefined);
            const source = shells.find((shell) => shell.id === event.dataTransfer.getData(SKY_SHELL_MIME));
            if (source) reorder(source, entity.skyShell!.order);
          }}
        >
          <button
            className="sky-composition-thumbnail"
            type="button"
            title={`Edit ${entity.name}`}
            onClick={() => void execute({
              id: crypto.randomUUID(), kind: 'setSelection', entityIds: [entity.id],
            }).then((committed) => { if (committed) setSkyCompositionSelected(false); })}
          >
            <SkyShellThumbnail entity={entity} runtime={runtime} />
          </button>
          <Text className="sky-composition-name" size="xs" title={entity.name}>
            {index + 1}. {entity.name}
          </Text>
          <Group gap={2} justify="space-between" wrap="nowrap">
            <ActionIcon aria-label={`Move ${entity.name} earlier`} disabled={disabled || index === 0}
              size="xs" variant="subtle" onClick={() => reorder(entity, index - 1)}>
              <CaretLeftIcon size={13} />
            </ActionIcon>
            <Text c="dimmed" size="xs">Drag</Text>
            <ActionIcon aria-label={`Move ${entity.name} later`} disabled={disabled || index === shells.length - 1}
              size="xs" variant="subtle" onClick={() => reorder(entity, index + 1)}>
              <CaretRightIcon size={13} />
            </ActionIcon>
            <ActionIcon aria-label={`Remove ${entity.name}`} color="red" disabled={disabled}
              size="xs" title={`Remove ${entity.name}`} variant="subtle" onClick={() => void execute({
                id: crypto.randomUUID(), kind: 'deleteEntities', entityIds: [entity.id],
              })}>
              <TrashIcon size={13} />
            </ActionIcon>
          </Group>
        </div>;
      })}
    </div>
  </Stack>;
}

function SkyShellThumbnail({ entity, runtime }: { entity: EditorEntity; runtime?: AssetThumbnailRuntime }) {
  const [source, setSource] = useState<string>();
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    if (!runtime || !entity.asset || !entity.skyShell) return;
    const controller = new AbortController();
    setSource(undefined);
    setFailed(false);
    void runtime.get(entity.asset.id, 'sky', entity.skyShell.sourceShellIndex, controller.signal)
      .then(setSource)
      .catch((cause: unknown) => {
        if (!(cause instanceof DOMException && cause.name === 'AbortError')) setFailed(true);
      });
    return () => controller.abort();
  }, [entity.asset?.id, entity.skyShell?.sourceShellIndex, runtime]);
  return source
    ? <img alt="" draggable={false} src={source} />
    : <Text c={failed ? 'red' : 'dimmed'} size="xs">{failed ? 'Unavailable' : 'Loading…'}</Text>;
}

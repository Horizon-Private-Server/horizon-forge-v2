import { CaretLeftIcon } from '@phosphor-icons/react/dist/csr/CaretLeft';
import { CaretRightIcon } from '@phosphor-icons/react/dist/csr/CaretRight';
import { DotsThreeVerticalIcon } from '@phosphor-icons/react/dist/csr/DotsThreeVertical';
import { MinusIcon } from '@phosphor-icons/react/dist/csr/Minus';
import { PencilSimpleIcon } from '@phosphor-icons/react/dist/csr/PencilSimple';
import { PlusIcon } from '@phosphor-icons/react/dist/csr/Plus';
import { TrashIcon } from '@phosphor-icons/react/dist/csr/Trash';
import { ActionIcon, Button, Group, Menu, Modal, Stack, Switch, Text, TextInput } from '@mantine/core';
import type { TreeNodeData } from '@mantine/core';
import { useEffect, useMemo, useState } from 'react';

import type { EditorEntity, EditorGroup, EditorMapGroup } from '../../types/EditorRuntime.js';
import { errorMessage } from '../../utils/Errors.ts';
import {
  DEFAULT_HIDE_EMPTY_GROUPS, HIDE_EMPTY_GROUPS_KEY, readHideEmptyGroups,
} from '../../utils/GroupVisibility.ts';
import { entityTreeKind, entityTreeText } from '../../utils/SceneSelection.ts';
import {
  buildSceneAreaGroups,
  buildSceneSemanticGroups,
  sceneGroupDropTargets,
  sceneGroupMemberIds,
  sceneGroupMemberValue,
  sceneGroupPage,
  sceneGroupSelectedValues,
  sceneGroupSelection,
  sceneGroupValue,
} from '../../utils/SceneGroups.ts';
import type { SceneSemanticGroup } from '../../utils/SceneGroups.ts';
import { AreaGroupsTree } from './AreaGroupsTree.tsx';
import { useEditor } from './EditorContext.ts';
import { EditorEmptyState, EditorPanel, EditorTree } from './EditorPrimitives.tsx';
import { groupTreeKind, SceneTreeLabel, SceneTreeNode } from './SceneTreeNodes.tsx';

export function GroupsPanel() {
  const {
    project, setCameraFocus, execute, busy, setSplinePointSelection, setSkyCompositionSelected,
  } = useEditor();
  const [groupPages, setGroupPages] = useState<Record<string, number>>({});
  const [groupEditor, setGroupEditor] = useState<{ groupId?: string; name: string }>();
  const [hideEmptyGroups, setHideEmptyGroups] = useState(DEFAULT_HIDE_EMPTY_GROUPS);
  const [preferenceError, setPreferenceError] = useState<string>();
  useEffect(() => {
    let disposed = false;
    void window.forge.getSettings().then((settings) => {
      if (!disposed) setHideEmptyGroups(readHideEmptyGroups(settings.entries));
    }).catch((cause) => {
      if (!disposed) setPreferenceError(errorMessage(cause));
    });
    return () => { disposed = true; };
  }, []);
  const areaModel = useMemo(() => {
    const model = buildSceneAreaGroups(project.entities, project.references, '');
    if (!hideEmptyGroups) return model;
    const groups = model.groups.filter((group) => group.members.length > 0);
    return { groups, matched: groups.length + groups.reduce((total, group) => total + group.members.length, 0) };
  }, [hideEmptyGroups, project.entities, project.references]);
  const areaSelectionModel = useMemo(() => buildSceneAreaGroups(project.entities, project.references, ''),
    [project.entities, project.references]);
  const visibleMapGroups = useMemo(() => project.mapGroups.filter(
    (group) => !hideEmptyGroups || group.members.length > 0 || group.missingSourceIndices.length > 0,
  ), [hideEmptyGroups, project.mapGroups]);
  const mapGroupDefinitions = useMemo<EditorGroup[]>(() => visibleMapGroups.map((group) => ({
    id: mapGroupId(group),
    name: `${mapGroupKindLabel(group.kind)} group #${group.sourceIndex}`,
    members: group.members,
    missingMembers: [],
  })), [visibleMapGroups]);
  const mapGroupsById = useMemo(() => new Map(visibleMapGroups.map((group) => [mapGroupId(group), group])),
    [visibleMapGroups]);
  const mapModel = useMemo(() => buildSceneSemanticGroups(
    mapGroupDefinitions, project.entities, '',
  ), [mapGroupDefinitions, project.entities]);
  const mapSelectionModel = useMemo(() => buildSceneSemanticGroups(
    mapGroupDefinitions, project.entities, '',
  ), [mapGroupDefinitions, project.entities]);
  const mapMemberIds = useMemo(() => sceneGroupMemberIds(mapModel.groups), [mapModel.groups]);
  const mapSelected = useMemo(() => sceneGroupSelectedValues(
    project.selection, mapMemberIds, mapSelectionModel.groups,
  ), [mapMemberIds, mapSelectionModel.groups, project.selection]);
  const model = useMemo(() => buildSceneSemanticGroups(project.groups, project.entities, ''),
    [project.entities, project.groups]);
  const selectionModel = useMemo(() => buildSceneSemanticGroups(project.groups, project.entities, ''),
    [project.entities, project.groups]);
  const memberIds = useMemo(() => sceneGroupMemberIds(model.groups), [model.groups]);
  const selected = useMemo(() => sceneGroupSelectedValues(
    project.selection, memberIds, selectionModel.groups,
  ), [project.selection, memberIds, selectionModel.groups]);
  const dropTargets = useMemo(() => sceneGroupDropTargets(model.groups), [model.groups]);
  const nodes = useMemo<TreeNodeData[]>(() => model.groups.map((group) => {
    const page = sceneGroupPage(group.members, groupPages[group.id] ?? 0);
    const known = (selectionModel.groups.find((candidate) => candidate.id === group.id)?.members ?? [])
      .flatMap((member) => member.entity ? [member.entity] : []);
    return {
      value: sceneGroupValue(group.id),
      label: <SemanticGroupNode
        group={group}
        entities={known}
        page={page.page}
        pageCount={page.pageCount}
        pageStart={page.start}
        pageEnd={page.end}
        disabled={busy}
        onPageChange={(next) => setGroupPages((current) => ({ ...current, [group.id]: next }))}
        onRename={() => setGroupEditor({ groupId: group.id, name: group.name })}
      />,
      nodeProps: { selectable: true },
      children: page.values.map((member) => member.entity ? {
        value: sceneGroupMemberValue(group.id, member.entityId),
        label: <SceneTreeNode
          entities={[member.entity]}
          disabled={busy}
          extraActions={<RemoveGroupMemberButton
            disabled={busy}
            entityId={member.entityId}
            groupId={group.id}
            label={member.entity.name}
          />}
        >
          <SceneTreeLabel kind={entityTreeKind(member.entity)} dot>{entityTreeText(member.entity)}</SceneTreeLabel>
        </SceneTreeNode>,
        nodeProps: { selectable: true },
      } : {
        value: `missing:${group.id}:${member.entityId}`,
        label: <span className="scene-tree-node">
          <SceneTreeLabel kind="object" dot>Missing entity {member.entityId} [missing, invalid]</SceneTreeLabel>
          <span className="scene-tree-actions">
            <RemoveGroupMemberButton
              disabled={busy}
              entityId={member.entityId}
              groupId={group.id}
              label={`missing entity ${member.entityId}`}
            />
          </span>
        </span>,
        nodeProps: { selectable: false },
      }),
    };
  }), [busy, groupPages, model.groups, selectionModel.groups]);
  const mapNodes = useMemo<TreeNodeData[]>(() => mapModel.groups.map((group) => {
    const page = sceneGroupPage(group.members, groupPages[group.id] ?? 0);
    const source = mapGroupsById.get(group.id)!;
    const known = (mapSelectionModel.groups.find((candidate) => candidate.id === group.id)?.members ?? [])
      .flatMap((member) => member.entity ? [member.entity] : []);
    return {
      value: sceneGroupValue(group.id),
      label: <MapGroupNode
        group={group}
        source={source}
        entities={known}
        page={page.page}
        pageCount={page.pageCount}
        pageStart={page.start}
        pageEnd={page.end}
        disabled={busy}
        onPageChange={(next) => setGroupPages((current) => ({ ...current, [group.id]: next }))}
      />,
      nodeProps: { selectable: true },
      children: page.values.flatMap((member) => member.entity ? [{
        value: sceneGroupMemberValue(group.id, member.entityId),
        label: <SceneTreeNode entities={[member.entity]} disabled={busy}>
          <SceneTreeLabel kind={entityTreeKind(member.entity)} dot>{entityTreeText(member.entity)}</SceneTreeLabel>
        </SceneTreeNode>,
        nodeProps: { selectable: true },
      }] : []),
    };
  }), [busy, groupPages, mapGroupsById, mapModel.groups, mapSelectionModel.groups]);

  const submitGroup = async () => {
    if (!groupEditor?.name.trim()) return;
    const succeeded = await execute(groupEditor.groupId
      ? {
        id: crypto.randomUUID(), kind: 'renameGroup', entityIds: [],
        groupId: groupEditor.groupId, text: groupEditor.name.trim(),
      }
      : { id: crypto.randomUUID(), kind: 'createGroup', entityIds: [], text: groupEditor.name.trim() });
    if (succeeded) setGroupEditor(undefined);
  };

  return <EditorPanel label="Groups">
    <Stack gap="xs">
      <Group gap="xs" justify="space-between" wrap="nowrap">
        <Group gap="xs" wrap="nowrap">
          <Button
            aria-label="New group"
            disabled={busy}
            leftSection={<PlusIcon size={14} />}
            size="xs"
            title="New group"
            variant="default"
            onClick={() => setGroupEditor({ name: 'New group' })}
          >New</Button>
          <Switch
            aria-label="Hide empty groups"
            checked={hideEmptyGroups}
            label="Hide empty"
            size="xs"
            onChange={(event) => {
              const value = event.currentTarget.checked;
              setHideEmptyGroups(value);
              setPreferenceError(undefined);
              void window.forge.setSetting(HIDE_EMPTY_GROUPS_KEY, value).catch((cause) => {
                setHideEmptyGroups(!value);
                setPreferenceError(errorMessage(cause));
              });
            }}
          />
        </Group>
        <Text size="xs" c="dimmed">{model.matched + areaModel.matched + mapModel.matched} matches</Text>
      </Group>
      <Text c="dimmed" size="xs">
        Areas and map groups come from the level and are baked. Custom groups exist only in Forge.
      </Text>
      {preferenceError && <Text c="red" size="xs">Could not save group visibility: {preferenceError}</Text>}
      <Text fw={600} size="xs">Custom groups</Text>
      {nodes.length
        ? <EditorTree
          label="Custom groups"
          nodes={nodes}
          draggableValues={memberIds}
          dropTargetValues={busy ? undefined : dropTargets}
          selected={selected}
          multiple
          onEntityDrop={(targetValue, entityId) => {
            const group = project.groups.find((candidate) => sceneGroupValue(candidate.id) === targetValue);
            if (!group || group.members.includes(entityId)) return;
            void execute({
              id: crypto.randomUUID(), kind: 'addGroupMembers', entityIds: [entityId], groupId: group.id,
            });
          }}
          onActivate={(value) => {
            const group = selectionModel.groups.find((candidate) => sceneGroupValue(candidate.id) === value);
            const entityId = memberIds.get(value) ?? group?.members.find((member) => member.entity)?.entityId;
            if (entityId) setCameraFocus({ entityId });
          }}
          onSelectionChange={(values, changedValue) => {
            setSkyCompositionSelected(false);
            setSplinePointSelection([]);
            void execute({
              id: crypto.randomUUID(), kind: 'setSelection',
              entityIds: sceneGroupSelection(values, selectionModel.groups, memberIds, changedValue),
            });
          }}
        />
        : <EditorEmptyState message="No matching custom groups or objects." />}
      <Text fw={600} size="xs">Areas</Text>
      <AreaGroupsTree groups={areaModel.groups} selectionGroups={areaSelectionModel.groups} />
      <Text fw={600} size="xs">Map groups</Text>
      {mapNodes.length
        ? <EditorTree
          label="Map groups"
          nodes={mapNodes}
          draggableValues={mapMemberIds}
          selected={mapSelected}
          multiple
          onActivate={(value) => {
            const group = mapSelectionModel.groups.find((candidate) => sceneGroupValue(candidate.id) === value);
            const entityId = mapMemberIds.get(value) ?? group?.members.find((member) => member.entity)?.entityId;
            if (entityId) setCameraFocus({ entityId });
          }}
          onSelectionChange={(values, changedValue) => {
            setSkyCompositionSelected(false);
            setSplinePointSelection([]);
            void execute({
              id: crypto.randomUUID(), kind: 'setSelection',
              entityIds: sceneGroupSelection(
                values, mapSelectionModel.groups, mapMemberIds, changedValue,
              ),
            });
          }}
        />
        : <EditorEmptyState message="No matching map groups." />}
    </Stack>
    <Modal
      opened={groupEditor !== undefined}
      onClose={() => setGroupEditor(undefined)}
      title={groupEditor?.groupId ? 'Rename group' : 'Create group'}
      size="sm"
    >
      <form onSubmit={(event) => { event.preventDefault(); void submitGroup(); }}>
        <Stack>
          <TextInput
            autoFocus
            label="Name"
            maxLength={256}
            value={groupEditor?.name ?? ''}
            onChange={(event) => {
              const name = event.currentTarget.value;
              setGroupEditor((current) => current ? { ...current, name } : current);
            }}
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setGroupEditor(undefined)}>Cancel</Button>
            <Button disabled={busy || !groupEditor?.name.trim()} type="submit">
              {groupEditor?.groupId ? 'Rename' : 'Create'}
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  </EditorPanel>;
}

function MapGroupNode({
  group, source, entities, page, pageCount, pageStart, pageEnd, disabled, onPageChange,
}: {
  group: SceneSemanticGroup;
  source: EditorMapGroup;
  entities: readonly EditorEntity[];
  page: number;
  pageCount: number;
  pageStart: number;
  pageEnd: number;
  disabled: boolean;
  onPageChange(page: number): void;
}) {
  const unresolved = source.missingSourceIndices.length;
  const label = <SceneTreeLabel kind={source.kind}>
    {group.name} ({source.members.length + unresolved}){unresolved ? ` [${unresolved} unresolved]` : ''}
  </SceneTreeLabel>;
  const actions = pageCount > 1 && <>
    <ActionIcon
      aria-label={`Previous page of ${group.name}`}
      disabled={page === 0}
      size="xs"
      title={`${pageStart + 1}–${pageEnd} of ${source.members.length}`}
      variant="subtle"
      onClick={(event) => { event.stopPropagation(); onPageChange(page - 1); }}
    ><CaretLeftIcon size={13} /></ActionIcon>
    <ActionIcon
      aria-label={`Next page of ${group.name}`}
      disabled={page + 1 >= pageCount}
      size="xs"
      title={`${pageStart + 1}–${pageEnd} of ${source.members.length}`}
      variant="subtle"
      onClick={(event) => { event.stopPropagation(); onPageChange(page + 1); }}
    ><CaretRightIcon size={13} /></ActionIcon>
  </>;
  return entities.length
    ? <SceneTreeNode entities={entities} disabled={disabled} extraActions={actions}>{label}</SceneTreeNode>
    : <span className="scene-tree-node">{label}<span className="scene-tree-actions">{actions}</span></span>;
}

function SemanticGroupNode({
  group, entities, page, pageCount, pageStart, pageEnd, disabled, onPageChange, onRename,
}: {
  group: SceneSemanticGroup;
  entities: readonly EditorEntity[];
  page: number;
  pageCount: number;
  pageStart: number;
  pageEnd: number;
  disabled: boolean;
  onPageChange(page: number): void;
  onRename(): void;
}) {
  const { project, execute } = useEditor();
  const memberIds = new Set(project.groups.find((candidate) => candidate.id === group.id)?.members ?? []);
  const selectedToAdd = project.selection.filter((entityId) => !memberIds.has(entityId));
  const groupIndex = project.groups.findIndex((candidate) => candidate.id === group.id);
  const missingCount = group.members.filter((member) => member.missing).length;
  const label = <SceneTreeLabel kind={groupTreeKind(entities)}>
    {group.name} ({group.members.length}){missingCount ? ` [${missingCount} missing]` : ''}
  </SceneTreeLabel>;
  const actions = <>
    {pageCount > 1 && <>
      <ActionIcon
        aria-label={`Previous page of ${group.name}`}
        disabled={page === 0}
        size="xs"
        title={`${pageStart + 1}–${pageEnd} of ${group.members.length}`}
        variant="subtle"
        onClick={(event) => { event.stopPropagation(); onPageChange(page - 1); }}
      ><CaretLeftIcon size={13} /></ActionIcon>
      <ActionIcon
        aria-label={`Next page of ${group.name}`}
        disabled={page + 1 >= pageCount}
        size="xs"
        title={`${pageStart + 1}–${pageEnd} of ${group.members.length}`}
        variant="subtle"
        onClick={(event) => { event.stopPropagation(); onPageChange(page + 1); }}
      ><CaretRightIcon size={13} /></ActionIcon>
    </>}
    <ActionIcon
      aria-label={`Add selection to ${group.name}`}
      disabled={disabled || selectedToAdd.length === 0}
      size="xs"
      title="Add current selection"
      variant="subtle"
      onClick={(event) => {
        event.stopPropagation();
        void execute({
          id: crypto.randomUUID(), kind: 'addGroupMembers', entityIds: selectedToAdd, groupId: group.id,
        });
      }}
    ><PlusIcon size={13} /></ActionIcon>
    <Menu position="bottom-end" withinPortal>
      <Menu.Target>
        <ActionIcon
          aria-label={`Actions for ${group.name}`}
          disabled={disabled}
          size="xs"
          title="Group actions"
          variant="subtle"
          onClick={(event) => event.stopPropagation()}
        ><DotsThreeVerticalIcon size={13} /></ActionIcon>
      </Menu.Target>
      <Menu.Dropdown onClick={(event) => event.stopPropagation()}>
        <Menu.Item leftSection={<PencilSimpleIcon size={14} />} onClick={onRename}>Rename</Menu.Item>
        <Menu.Item
          disabled={groupIndex <= 0}
          onClick={() => void execute({
            id: crypto.randomUUID(), kind: 'reorderGroup', entityIds: [],
            groupId: group.id, destinationOrder: groupIndex - 1,
          })}
        >Move earlier</Menu.Item>
        <Menu.Item
          disabled={groupIndex < 0 || groupIndex + 1 >= project.groups.length}
          onClick={() => void execute({
            id: crypto.randomUUID(), kind: 'reorderGroup', entityIds: [],
            groupId: group.id, destinationOrder: groupIndex + 1,
          })}
        >Move later</Menu.Item>
        <Menu.Divider />
        <Menu.Item
          color="red"
          leftSection={<TrashIcon size={14} />}
          onClick={() => void execute({
            id: crypto.randomUUID(), kind: 'deleteGroup', entityIds: [], groupId: group.id,
          })}
        >Delete group</Menu.Item>
      </Menu.Dropdown>
    </Menu>
  </>;
  return entities.length
    ? <SceneTreeNode entities={entities} disabled={disabled} extraActions={actions}>{label}</SceneTreeNode>
    : <span className="scene-tree-node">{label}<span className="scene-tree-actions">{actions}</span></span>;
}

function RemoveGroupMemberButton({ disabled, entityId, groupId, label }: {
  disabled: boolean;
  entityId: string;
  groupId: string;
  label: string;
}) {
  const { execute } = useEditor();
  return <ActionIcon
    aria-label={`Remove ${label} from group`}
    disabled={disabled}
    size="xs"
    title="Remove from group"
    variant="subtle"
    onClick={(event) => {
      event.stopPropagation();
      void execute({
        id: crypto.randomUUID(), kind: 'removeGroupMembers', entityIds: [entityId], groupId,
      });
    }}
  ><MinusIcon size={13} /></ActionIcon>;
}

function mapGroupId(group: EditorMapGroup): string {
  return `map:${group.kind}:${group.sourceIndex}`;
}

function mapGroupKindLabel(kind: EditorMapGroup['kind']): string {
  return kind === 'moby' ? 'Moby' : kind === 'tie' ? 'Tie' : 'Shrub';
}

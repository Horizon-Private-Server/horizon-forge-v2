import type { TreeNodeData } from '@mantine/core';
import { useMemo, useState } from 'react';

import { entityTreeKind, entityTreeText } from '../../utils/SceneSelection.ts';
import {
  sceneGroupMemberIds,
  sceneGroupMemberValue,
  sceneGroupPage,
  sceneGroupSelectedValues,
  sceneGroupSelection,
  sceneGroupValue,
} from '../../utils/SceneGroups.ts';
import type { SceneAreaGroup, SceneSemanticGroup } from '../../utils/SceneGroups.ts';
import { useEditor } from './EditorContext.ts';
import { EditorEmptyState, EditorTree } from './EditorPrimitives.tsx';
import { GroupPageActions } from './GroupPageActions.tsx';
import { SceneTreeLabel, SceneTreeNode } from './SceneTreeNodes.tsx';

export function AreaGroupsTree({ groups }: {
  groups: readonly SceneAreaGroup[];
}) {
  const {
    project, setCameraFocus, execute, busy, setSplinePointSelection, setSkyCompositionSelected,
  } = useEditor();
  const [pages, setPages] = useState<Record<string, number>>({});
  const semanticGroups = useMemo(() => areaSemanticGroups(groups), [groups]);
  const memberIds = useMemo(() => sceneGroupMemberIds(semanticGroups), [semanticGroups]);
  const selected = useMemo(() => sceneGroupSelectedValues(
    project.selection, memberIds, semanticGroups,
  ), [memberIds, project.selection, semanticGroups]);
  const nodes = useMemo<TreeNodeData[]>(() => groups.map((group) => {
    const page = sceneGroupPage(group.members, pages[group.area.id] ?? 0);
    const missing = group.members.filter((member) => member.missing).length;
    const known = group.members.flatMap((member) => member.entity ? [member.entity] : []);
    const label = <SceneTreeLabel kind="area">
      {entityTreeText(group.area)} ({group.members.length}){missing ? ` [${missing} unresolved]` : ''}
    </SceneTreeLabel>;
    return {
      value: sceneGroupValue(group.area.id),
      label: known.length ? <SceneTreeNode entities={known} disabled={busy} extraActions={
        <GroupPageActions
          label={group.area.name}
          page={page.page}
          pageCount={page.pageCount}
          start={page.start}
          end={page.end}
          total={group.members.length}
          onChange={(next) => setPages((current) => ({ ...current, [group.area.id]: next }))}
        />}
      >
        {label}
      </SceneTreeNode> : <span className="scene-tree-node">{label}</span>,
      nodeProps: { selectable: known.length > 0 },
      children: page.values.map((member) => ({
        value: sceneGroupMemberValue(group.area.id, member.entityId),
        label: member.entity
          ? <SceneTreeNode entities={[member.entity]} disabled={busy}>
            <SceneTreeLabel kind={entityTreeKind(member.entity)} dot>
              {entityTreeText(member.entity)}
            </SceneTreeLabel>
          </SceneTreeNode>
          : <span className="scene-tree-node">
            <SceneTreeLabel kind="object" dot>
              Missing reference {member.entityId} [missing, invalid]
            </SceneTreeLabel>
          </span>,
        nodeProps: { selectable: Boolean(member.entity) },
      })),
    };
  }), [busy, groups, pages, semanticGroups]);

  if (!nodes.length) return <EditorEmptyState message="No matching areas." />;
  return <EditorTree
    label="Areas"
    nodes={nodes}
    draggableValues={memberIds}
    selected={selected}
    multiple
    onActivate={(value) => {
      const group = semanticGroups.find((candidate) => sceneGroupValue(candidate.id) === value);
      const entityId = memberIds.get(value) ?? group?.members.find((member) => member.entity)?.entityId;
      if (entityId) setCameraFocus({ entityId });
    }}
    onSelectionChange={(values, changedValue) => {
      setSkyCompositionSelected(false);
      setSplinePointSelection([]);
      void execute({
        id: crypto.randomUUID(),
        kind: 'setSelection',
        entityIds: sceneGroupSelection(values, semanticGroups, memberIds, changedValue),
      });
    }}
  />;
}

function areaSemanticGroups(groups: readonly SceneAreaGroup[]): SceneSemanticGroup[] {
  return groups.map((group) => ({
    id: group.area.id,
    name: group.area.name,
    members: group.members,
  }));
}

import { EyeIcon } from '@phosphor-icons/react/dist/csr/Eye';
import { EyeSlashIcon } from '@phosphor-icons/react/dist/csr/EyeSlash';
import { PowerIcon } from '@phosphor-icons/react/dist/csr/Power';
import { ActionIcon, Badge } from '@mantine/core';
import type { ReactNode } from 'react';

import type { EditorEntity } from '../../types/EditorRuntime.js';
import type { SceneTreeKind } from '../../types/SceneTree.js';
import { entityTreeKind } from '../../utils/SceneSelection.ts';
import { DEFAULT_SCENE_TREE_COLORS, SCENE_TREE_LABELS } from '../../utils/SceneTreeColors.ts';
import { useEditor } from './EditorContext.ts';

export function SceneTreeNode({ entities, disabled, children, extraActions }: {
  entities: readonly EditorEntity[];
  disabled: boolean;
  children: ReactNode;
  extraActions?: ReactNode;
}) {
  const { execute } = useEditor();
  const ids = entities.map((entity) => entity.id);
  const allHidden = entities.every((entity) => entity.state.hidden);
  const allDisabled = entities.every((entity) => entity.state.disabled);
  const locked = entities.some((entity) => entity.state.locked);
  const description = entities.length === 1 ? entities[0].name : `${entities.length} objects`;
  const change = (state: { hidden?: boolean; disabled?: boolean }) => void execute({
    id: crypto.randomUUID(),
    kind: 'setEntityState',
    entityIds: ids,
    state,
  });
  return <span className="scene-tree-node">
    {children}
    <span className="scene-tree-actions">
      <ActionIcon
        aria-label={`${allHidden ? 'Show' : 'Hide'} ${description}`}
        color={allHidden ? 'gray' : 'blue'}
        disabled={disabled || locked}
        size="xs"
        title={`${allHidden ? 'Show' : 'Hide'} ${description}`}
        variant="subtle"
        onClick={(event) => {
          event.stopPropagation();
          change({ hidden: !allHidden });
        }}
      >
        {allHidden ? <EyeSlashIcon size={13} /> : <EyeIcon size={13} />}
      </ActionIcon>
      <ActionIcon
        aria-label={`${allDisabled ? 'Enable' : 'Disable'} ${description}`}
        color={allDisabled ? 'gray' : 'teal'}
        disabled={disabled || locked}
        size="xs"
        title={`${allDisabled ? 'Enable' : 'Disable'} ${description}`}
        variant="subtle"
        onClick={(event) => {
          event.stopPropagation();
          change({ disabled: !allDisabled });
        }}
      >
        <PowerIcon size={13} weight={allDisabled ? 'regular' : 'fill'} />
      </ActionIcon>
      {extraActions}
    </span>
  </span>;
}

export function SceneTreeLabel({ kind, children, dot = false }: { kind: string; children: ReactNode; dot?: boolean }) {
  const normalizedKind = kind in SCENE_TREE_LABELS ? kind as SceneTreeKind : 'object';
  const label = kind === 'mixed' ? 'Mixed' : SCENE_TREE_LABELS[normalizedKind];
  const color = `var(--forge-scene-tree-${normalizedKind}, ${DEFAULT_SCENE_TREE_COLORS[normalizedKind]})`;
  return <span className="scene-tree-label">
    {dot
      ? <span
        aria-label={`${label} item`}
        className="scene-tree-dot"
        role="img"
        style={{ backgroundColor: color }}
        title={label}
      />
      : <Badge className="scene-tree-badge" size="xs" variant="outline" style={{ borderColor: color, color }}>
        {label}
      </Badge>}
    <span className="scene-tree-label-text">{children}</span>
  </span>;
}

export function groupTreeKind(entities: readonly EditorEntity[]): string {
  const kinds = new Set(entities.map(entityTreeKind));
  return kinds.size > 1 ? 'mixed' : kinds.values().next().value ?? 'object';
}

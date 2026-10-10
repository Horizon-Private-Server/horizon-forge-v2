import {
  Alert,
  Box,
  Progress,
  Stack,
  Text,
  TextInput,
  Tree,
  useTree,
} from '@mantine/core';
import type { TreeNodeData } from '@mantine/core';
import { useEffect, useMemo, useRef, useState } from 'react';
import type { KeyboardEvent, MouseEvent, ReactNode } from 'react';

import {
  clearEntityDrag, ENTITY_DRAG_MIME, readEntityDrag, writeEntityDrag,
} from '../../utils/EntityDrag.ts';
import { nextTreeSelection } from '../../utils/SceneSelection.ts';

interface EditorPanelProps {
  label: string;
  children: ReactNode;
}

export function EditorPanel({ label, children }: EditorPanelProps) {
  return <section aria-label={label} className="editor-panel">{children}</section>;
}

interface EditorFilterProps {
  label: string;
  value: string;
  onChange(value: string): void;
}

export function EditorFilter({ label, value, onChange }: EditorFilterProps) {
  return <TextInput
    aria-label={label}
    placeholder="Filter…"
    value={value}
    onChange={(event) => onChange(event.currentTarget.value)}
  />;
}

interface EditorTreeProps {
  label: string;
  nodes: TreeNodeData[];
  selected?: string[];
  multiple?: boolean;
  draggableValues?: ReadonlySet<string> | ReadonlyMap<string, string>;
  dropTargetValues?: ReadonlySet<string> | ReadonlyMap<string, string>;
  onActivate?(value: string): void;
  onEntityDrop?(targetValue: string, entityId: string): void;
  onSelectionChange?(values: string[], changedValue?: string): void;
}

export function EditorTree({
  label, nodes, selected = [], multiple = false, draggableValues, dropTargetValues,
  onActivate, onEntityDrop, onSelectionChange,
}: EditorTreeProps) {
  const tree = useTree({ selectedState: selected, multiple, onSelectedStateChange: onSelectionChange });
  const values = useMemo(() => selectableTreeValues(nodes), [nodes]);
  const valueSet = useMemo(() => new Set(values), [values]);
  const anchor = useRef<string | undefined>(undefined);
  const [dropping, setDropping] = useState<string>();

  useEffect(() => {
    if (!selected.length) anchor.current = undefined;
  }, [selected]);

  const select = (value: string, event: MouseEvent | KeyboardEvent) => {
    if (!onSelectionChange || !valueSet.has(value)) return;
    if (!multiple) {
      anchor.current = value;
      onSelectionChange([value], value);
      return;
    }
    const next = nextTreeSelection(selected, value, values, anchor.current, {
      toggle: event.ctrlKey || event.metaKey,
      range: event.shiftKey,
    });
    anchor.current = next.anchor;
    onSelectionChange(next.selected, value);
  };

  return <Tree
    aria-label={label}
    allowRangeSelection={false}
    className="editor-tree"
    data={nodes}
    levelOffset="lg"
    selectOnClick={false}
    tree={tree}
    onClick={(event) => {
      if (event.target === event.currentTarget && onSelectionChange) onSelectionChange([]);
    }}
    onKeyDownCapture={(event) => {
      if ((event.key === 'ArrowUp' || event.key === 'ArrowDown')
        && !event.shiftKey && !event.ctrlKey && !event.metaKey && onSelectionChange) {
        const root = event.currentTarget;
        // Mantine moves tree focus in its node keydown handler after this capture handler.
        queueMicrotask(() => {
          const item = (root.ownerDocument.activeElement as HTMLElement | null)
            ?.closest<HTMLElement>('[role="treeitem"]');
          const value = item?.dataset.value;
          if (!item || !root.contains(item) || !value || !valueSet.has(value)) return;
          anchor.current = value;
          onSelectionChange([value], value);
        });
        return;
      }
      if (event.key !== 'Enter' && event.key !== ' ') return;
      if ((event.target as HTMLElement).closest('button, input, select, textarea, a')) return;
      const value = (event.target as HTMLElement).closest<HTMLElement>('[role="treeitem"]')?.dataset.value;
      if (!value || !valueSet.has(value)) return;
      event.preventDefault();
      event.stopPropagation();
      select(value, event);
    }}
    renderNode={({ node, expanded, hasChildren, elementProps, tree: controller }) => {
      const dropTarget = dropTargetValues instanceof Map
        ? dropTargetValues.get(node.value)
        : dropTargetValues?.has(node.value) ? node.value : undefined;
      return <div
        {...elementProps}
        className={`${elementProps.className} editor-tree-row`}
        data-dropping={dropTarget !== undefined && dropping === dropTarget || undefined}
        draggable={draggableValues?.has(node.value)}
        onDragStart={(event) => {
          const entityId = draggableValues instanceof Map
            ? draggableValues.get(node.value)
            : draggableValues?.has(node.value) ? node.value : undefined;
          if (!entityId) {
            event.preventDefault();
            return;
          }
          writeEntityDrag(event.dataTransfer, entityId);
        }}
        onDragEnd={() => {
          clearEntityDrag();
          setDropping(undefined);
        }}
        onDragEnter={(event) => {
          if (!dropTarget || !event.dataTransfer.types.includes(ENTITY_DRAG_MIME)) return;
          event.preventDefault();
          setDropping(dropTarget);
        }}
        onDragLeave={(event) => {
          if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setDropping(undefined);
        }}
        onDragOver={(event) => {
          if (!dropTarget || !event.dataTransfer.types.includes(ENTITY_DRAG_MIME)) return;
          event.preventDefault();
          event.dataTransfer.dropEffect = 'copy';
          setDropping(dropTarget);
        }}
        onDrop={(event) => {
          if (!dropTarget) return;
          event.preventDefault();
          event.stopPropagation();
          setDropping(undefined);
          const entityId = readEntityDrag(event.dataTransfer);
          clearEntityDrag();
          if (entityId) onEntityDrop?.(dropTarget, entityId);
        }}
        onClick={(event) => {
          event.stopPropagation();
          if (event.detail > 1) return;
          select(node.value, event);
          event.currentTarget.closest<HTMLElement>('[role="treeitem"]')?.focus();
        }}
        onDoubleClick={(event) => {
          event.stopPropagation();
          if (valueSet.has(node.value)) onActivate?.(node.value);
        }}
      >
        {hasChildren
          ? <button
            aria-expanded={expanded}
            aria-label={expanded ? 'Collapse branch' : 'Expand branch'}
            className="editor-tree-chevron"
            type="button"
            onClick={(event) => {
              event.stopPropagation();
              controller.toggleExpanded(node.value);
            }}
          >{expanded ? '▾' : '▸'}</button>
          : <span aria-hidden="true" className="editor-tree-chevron" />}
        <span className="editor-tree-content">{node.label}</span>
      </div>;
    }}
  />;
}

function selectableTreeValues(nodes: TreeNodeData[]): string[] {
  return nodes.flatMap((node) => [
    ...(node.nodeProps?.selectable === true || !node.children?.length && node.nodeProps?.selectable !== false
      ? [node.value] : []),
    ...selectableTreeValues(node.children ?? []),
  ]);
}

export function EditorPropertyGrid({ children }: { children: ReactNode }) {
  return <Box component="dl" className="editor-property-grid">{children}</Box>;
}

export function EditorProperty({ label, children }: { label: string; children: ReactNode }) {
  return <><Text component="dt" c="dimmed">{label}</Text><Text component="dd">{children}</Text></>;
}

export function EditorDiagnosticList({ messages }: { messages: string[] }) {
  return messages.length
    ? <Stack component="ul" className="editor-diagnostics">{messages.map((message, index) => <Text component="li" key={`${index}:${message}`}>{message}</Text>)}</Stack>
    : <EditorEmptyState message="No diagnostics." />;
}

export function EditorProgressState({ label, completed, total }: { label: string; completed: number; total: number }) {
  const value = total > 0 ? Math.min(100, Math.max(0, completed / total * 100)) : 0;
  return <Stack role="status" aria-label={label}>
    <Text>{label}</Text>
    <Progress value={value} aria-label={`${label}: ${Math.round(value)}%`} />
  </Stack>;
}

export function EditorEmptyState({ message }: { message: string }) {
  return <Text c="dimmed" className="editor-empty" role="status">{message}</Text>;
}

export function EditorErrorState({ message, onClose }: { message: string; onClose?(): void }) {
  return <Alert color="red" title="Error" withCloseButton={Boolean(onClose)} onClose={onClose}>{message}</Alert>;
}

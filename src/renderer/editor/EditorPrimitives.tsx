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
import { useEffect, useMemo, useRef } from 'react';
import type { KeyboardEvent, MouseEvent, ReactNode } from 'react';

import { nextTreeSelection } from './EditorPanelState.ts';

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
  onActivate?(value: string): void;
  onSelectionChange?(values: string[]): void;
}

export function EditorTree({ label, nodes, selected = [], multiple = false, onActivate, onSelectionChange }: EditorTreeProps) {
  const tree = useTree({ selectedState: selected, multiple, onSelectedStateChange: onSelectionChange });
  const values = useMemo(() => selectableTreeValues(nodes), [nodes]);
  const valueSet = useMemo(() => new Set(values), [values]);
  const anchor = useRef<string | undefined>(undefined);

  useEffect(() => {
    if (!selected.length) anchor.current = undefined;
  }, [selected]);

  const select = (value: string, event: MouseEvent | KeyboardEvent) => {
    if (!onSelectionChange || !valueSet.has(value)) return;
    if (!multiple) {
      anchor.current = value;
      onSelectionChange([value]);
      return;
    }
    const next = nextTreeSelection(selected, value, values, anchor.current, {
      toggle: event.ctrlKey || event.metaKey,
      range: event.shiftKey,
    });
    anchor.current = next.anchor;
    onSelectionChange(next.selected);
  };

  return <Tree
    aria-label={label}
    allowRangeSelection={false}
    className="editor-tree"
    data={nodes}
    levelOffset="sm"
    selectOnClick={false}
    tree={tree}
    onClick={(event) => {
      if (event.target === event.currentTarget && onSelectionChange) onSelectionChange([]);
    }}
    onKeyDownCapture={(event) => {
      if (event.key !== 'Enter' && event.key !== ' ') return;
      const value = (event.target as HTMLElement).closest<HTMLElement>('[role="treeitem"]')?.dataset.value;
      if (!value || !valueSet.has(value)) return;
      event.preventDefault();
      event.stopPropagation();
      select(value, event);
    }}
    renderNode={({ node, expanded, hasChildren, elementProps, tree: controller }) => <div
      {...elementProps}
      className={`${elementProps.className} editor-tree-row`}
      onClick={(event) => {
        event.stopPropagation();
        if (event.detail > 1) return;
        if (hasChildren) controller.toggleExpanded(node.value);
        else select(node.value, event);
        event.currentTarget.closest<HTMLElement>('[role="treeitem"]')?.focus();
      }}
      onDoubleClick={(event) => {
        event.stopPropagation();
        if (valueSet.has(node.value)) onActivate?.(node.value);
      }}
    >
      <span aria-hidden="true" className="editor-tree-chevron">{hasChildren ? (expanded ? '▾' : '▸') : ''}</span>
      <span>{node.label}</span>
    </div>}
  />;
}

function selectableTreeValues(nodes: TreeNodeData[]): string[] {
  return nodes.flatMap((node) => node.children?.length
    ? selectableTreeValues(node.children)
    : node.nodeProps?.selectable === false ? [] : [node.value]);
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

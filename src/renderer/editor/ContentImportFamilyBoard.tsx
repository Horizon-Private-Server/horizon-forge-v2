import { Badge, Button, Group, Stack, Text, UnstyledButton } from '@mantine/core';
import { useMemo, useRef, useState } from 'react';
import type { DragEvent, MouseEvent } from 'react';

import type { ContentImportPlan } from '../../types/ContentImport.js';
import { nextTreeSelection } from '../../utils/SceneSelection.ts';

type FamilyAction = 'skip' | 'add' | 'replace';

interface ContentImportFamilyBoardProps {
  families: ContentImportPlan['families'];
  selections: Record<string, 'add' | 'replace'>;
  busy: boolean;
  onChange(selections: Record<string, 'add' | 'replace'>): void;
}

const FAMILY_DRAG_MIME = 'application/x-forge-content-import-families';
const ACTIONS: readonly FamilyAction[] = ['skip', 'add', 'replace'];

export function ContentImportFamilyBoard({
  families, selections, busy, onChange,
}: ContentImportFamilyBoardProps) {
  const visible = useMemo(
    () => families.filter((family) => family.family !== 'opaqueContent'),
    [families],
  );
  const selectableNames = useMemo(
    () => visible.filter((family) => family.selectable).map((family) => family.family),
    [visible],
  );
  const familiesByName = useMemo(
    () => new Map(visible.map((family) => [family.family, family])),
    [visible],
  );
  const [selected, setSelected] = useState<string[]>([]);
  const [dragged, setDragged] = useState<string[]>([]);
  const [dropTarget, setDropTarget] = useState<FamilyAction>();
  const anchor = useRef<string | undefined>(undefined);

  const modeFor = (family: string): FamilyAction => selections[family] ?? 'skip';
  const canAssign = (action: FamilyAction, names: readonly string[]) => names.length > 0
    && names.every((name) => {
      const family = familiesByName.get(name);
      return family?.selectable && (action === 'skip' || family.supportedMode === action
        || family.supportedMode === 'addAndReplace');
    });

  function selectFamily(family: string, event: MouseEvent) {
    const next = nextTreeSelection(selected, family, selectableNames, anchor.current, {
      toggle: event.ctrlKey || event.metaKey,
      range: event.shiftKey,
    });
    anchor.current = next.anchor;
    setSelected(next.selected);
  }

  function assign(action: FamilyAction, names = selected) {
    if (busy || !canAssign(action, names)) return;
    const next = { ...selections };
    for (const family of names) {
      if (action === 'skip') delete next[family];
      else next[family] = action;
    }
    onChange(next);
  }

  function beginDrag(event: DragEvent, family: string) {
    if (busy) return;
    const names = selected.includes(family) ? selected : [family];
    if (!selected.includes(family)) {
      anchor.current = family;
      setSelected(names);
    }
    event.dataTransfer.effectAllowed = 'move';
    event.dataTransfer.setData(FAMILY_DRAG_MIME, JSON.stringify(names));
    setDragged(names);
  }

  function droppedFamilies(event: DragEvent): string[] {
    try {
      const value: unknown = JSON.parse(event.dataTransfer.getData(FAMILY_DRAG_MIME));
      return Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string') : [];
    } catch {
      return [];
    }
  }

  return <Stack gap="xs">
    <Group justify="space-between" align="baseline">
      <Text fw={600} size="sm">Content selection</Text>
      <Text aria-live="polite" c="dimmed" size="xs">
        {selected.length ? `${selected.length} selected` : 'Click to select · Shift-click for a range'}
      </Text>
    </Group>
    <div className="content-import-board">
      {ACTIONS.map((action) => {
        const members = visible.filter((family) => family.selectable && modeFor(family.family) === action);
        const assignable = canAssign(action, selected);
        const addBlocked = action === 'add' && selected.length > 0 && !assignable;
        return <section
          aria-label={`${familyActionLabel(action)} import content`}
          className="content-import-drop-zone"
          data-action={action}
          data-dropping={dropTarget === action || undefined}
          key={action}
          onDragEnter={(event) => {
            if (!busy && canAssign(action, dragged)) {
              event.preventDefault();
              setDropTarget(action);
            }
          }}
          onDragLeave={(event) => {
            if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setDropTarget(undefined);
          }}
          onDragOver={(event) => {
            if (busy || !canAssign(action, dragged)) return;
            event.preventDefault();
            event.dataTransfer.dropEffect = 'move';
            setDropTarget(action);
          }}
          onDrop={(event) => {
            event.preventDefault();
            const names = droppedFamilies(event);
            setDropTarget(undefined);
            setDragged([]);
            assign(action, names);
          }}
        >
          <Group className="content-import-drop-header" justify="space-between" wrap="nowrap">
            <Stack gap={0}>
              <Group gap={6} wrap="nowrap">
                <Text fw={600} size="sm">{familyActionLabel(action)}</Text>
                <Badge color={familyActionColor(action)} size="xs" variant="light">{members.length}</Badge>
              </Group>
              <Text c="dimmed" size="xs">{familyActionDescription(action)}</Text>
            </Stack>
            <Button
              aria-label={`Move selected content to ${familyActionLabel(action)}`}
              disabled={busy || !assignable}
              size="compact-xs"
              title={addBlocked ? 'Some selected content can only be replaced.' : undefined}
              variant="subtle"
              onClick={() => assign(action)}
            >Move selected</Button>
          </Group>
          <div className="content-import-family-list">
            {members.map((family) => <FamilyBadge
              action={action}
              family={family}
              selected={selected.includes(family.family)}
              busy={busy}
              key={family.family}
              onClick={selectFamily}
              onDragStart={beginDrag}
              onDragEnd={() => {
                setDragged([]);
                setDropTarget(undefined);
              }}
            />)}
            {members.length === 0 && <Text c="dimmed" size="xs">Drop content here</Text>}
          </div>
        </section>;
      })}
    </div>
    {visible.some((family) => !family.selectable) && <section aria-label="Unavailable donor content">
      <Text c="dimmed" fw={600} mb={4} size="xs">Unavailable in donor</Text>
      <div className="content-import-family-list">
        {visible.filter((family) => !family.selectable).map((family) => <UnstyledButton
          aria-label={`${family.label} unavailable: ${familyUnavailableReason(family.sourceState)}`}
          className="content-import-family-badge"
          disabled
          key={family.family}
          title={familyUnavailableReason(family.sourceState)}
        >
          <Badge color="gray" variant="outline">{family.label}</Badge>
        </UnstyledButton>)}
      </div>
    </section>}
  </Stack>;
}

function FamilyBadge({ action, family, selected, busy, onClick, onDragStart, onDragEnd }: {
  action: FamilyAction;
  family: ContentImportPlan['families'][number];
  selected: boolean;
  busy: boolean;
  onClick(family: string, event: MouseEvent): void;
  onDragStart(event: DragEvent, family: string): void;
  onDragEnd(): void;
}) {
  const label = family.label;
  return <UnstyledButton
    aria-label={`${label}, ${familyActionLabel(action)}`}
    aria-pressed={selected}
    className="content-import-family-badge"
    data-selected={selected || undefined}
    disabled={busy}
    draggable={!busy}
    title={`${label} · ${familyActionLabel(action)}${selected ? ' · selected' : ''}`}
    onClick={(event) => onClick(family.family, event)}
    onDragEnd={onDragEnd}
    onDragStart={(event) => onDragStart(event, family.family)}
  >
    <Badge color={familyActionColor(action)} variant={selected ? 'filled' : 'light'}>{label}</Badge>
  </UnstyledButton>;
}

function familyActionLabel(action: FamilyAction): string {
  if (action === 'add') return 'Add';
  if (action === 'replace') return 'Replace';
  return 'Skip';
}

function familyActionDescription(action: FamilyAction): string {
  if (action === 'add') return 'Keep existing target content';
  if (action === 'replace') return 'Replace matching target content';
  return 'Leave out of this import';
}

function familyActionColor(action: FamilyAction): string {
  if (action === 'add') return 'teal';
  if (action === 'replace') return 'orange';
  return 'gray';
}

function familyUnavailableReason(state: ContentImportPlan['families'][number]['sourceState']): string {
  if (state === 'empty') return 'Empty in donor map';
  if (state === 'absent') return 'Not present in donor map';
  if (state === 'malformed') return 'Donor data needs repair or additional format support';
  return 'Unavailable';
}

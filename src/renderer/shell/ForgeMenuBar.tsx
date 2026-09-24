import { Menu, UnstyledButton } from '@mantine/core';
import type { ReactNode } from 'react';

import type { KeybindingCommandId, KeybindingMap } from '../../types/Keybindings.js';
import type { EditorSnapshot, ForgeAction, ForgeWindowAction } from '../../types/ForgeApi.js';
import { formatKeybinding } from '../../utils/Keybindings.ts';

interface ForgeMenuBarProps {
  keybindings: KeybindingMap;
  project?: EditorSnapshot;
  onAction: (action: ForgeAction) => void;
}

export function ForgeMenuBar({ keybindings, project, onAction }: ForgeMenuBarProps) {
  const selectedIds = new Set(project?.selection ?? []);
  const selected = project?.entities.filter((entity) => selectedIds.has(entity.id)) ?? [];
  const mutableSelection = selected.length > 0 && selected.every((entity) => !entity.state.locked);
  const windowAction = (action: ForgeWindowAction) => window.forge.runWindowAction(action);
  const shortcut = (id: KeybindingCommandId) => formatKeybinding(keybindings[id]);

  return <nav className="forge-menu-bar" aria-label="Application menu">
    <TopMenu label="File">
      <Item shortcut={shortcut('project.new')} onClick={() => onAction('newProject')}>New Project…</Item>
      <Item shortcut={shortcut('project.open')} onClick={() => onAction('openProject')}>Open Project…</Item>
      <Item shortcut={shortcut('project.save')} disabled={!project} onClick={() => onAction('saveProject')}>Save</Item>
      <Item onClick={() => onAction('projects')}>Project Hub</Item>
      <Menu.Divider />
      <Item onClick={() => windowAction('quit')}>Quit</Item>
    </TopMenu>

    <TopMenu label="Edit">
      <Item shortcut={shortcut('edit.undo')} disabled={!project?.canUndo} onClick={() => onAction('undoEditor')}>Undo</Item>
      <Item shortcut={shortcut('edit.redo')} disabled={!project?.canRedo} onClick={() => onAction('redoEditor')}>Redo</Item>
      <Menu.Divider />
      <Item shortcut={shortcut('edit.duplicate')} disabled={!mutableSelection}
        onClick={() => onAction('duplicateEntities')}>Duplicate</Item>
      <Item shortcut={shortcut('edit.delete')} disabled={!mutableSelection}
        onClick={() => onAction('deleteEntities')}>Delete</Item>
      <Menu.Divider />
      <Item shortcut={shortcut('edit.copy')} disabled={selected.length === 0}
        onClick={() => onAction('copyEntities')}>Copy</Item>
      <Item shortcut={shortcut('edit.paste')} disabled={!project?.canPaste}
        onClick={() => onAction('pasteEntities')}>Paste</Item>
    </TopMenu>

    <TopMenu label="View">
      <Menu.Sub>
        <Menu.Sub.Target><Menu.Sub.Item disabled={!project}>Panels</Menu.Sub.Item></Menu.Sub.Target>
        <Menu.Sub.Dropdown>
          <Item onClick={() => onAction('showViewport')}>Viewport</Item>
          <Item onClick={() => onAction('showSceneTree')}>Scene</Item>
          <Item onClick={() => onAction('showProperties')}>Properties</Item>
          <Item onClick={() => onAction('showDiagnostics')}>Diagnostics</Item>
          <Item onClick={() => onAction('showBuild')}>Build</Item>
        </Menu.Sub.Dropdown>
      </Menu.Sub>
      <Item disabled={!project} onClick={() => onAction('resetLayout')}>Reset Layout</Item>
      <Menu.Divider />
      <Item shortcut="Ctrl+0" onClick={() => windowAction('resetZoom')}>Actual Size</Item>
      <Item shortcut="Ctrl++" onClick={() => windowAction('zoomIn')}>Zoom In</Item>
      <Item shortcut="Ctrl+-" onClick={() => windowAction('zoomOut')}>Zoom Out</Item>
      <Menu.Divider />
      <Item shortcut="F11" onClick={() => windowAction('toggleFullscreen')}>Toggle Full Screen</Item>
    </TopMenu>

    <TopMenu label="Window">
      <Item onClick={() => windowAction('minimize')}>Minimize</Item>
      <Item onClick={() => windowAction('toggleMaximize')}>Maximize / Restore</Item>
    </TopMenu>

    <TopMenu label="Forge">
      <Item onClick={() => onAction('setup')}>Setup…</Item>
      <Item shortcut={shortcut('app.settings')} onClick={() => onAction('settings')}>Settings…</Item>
    </TopMenu>
  </nav>;
}

function TopMenu({ label, children }: { label: string; children: ReactNode }) {
  return <Menu position="bottom-start" offset={0} withinPortal={false} loop>
    <Menu.Target>
      <UnstyledButton className="forge-menu-trigger">{label}</UnstyledButton>
    </Menu.Target>
    <Menu.Dropdown>{children}</Menu.Dropdown>
  </Menu>;
}

function Item({ shortcut, children, disabled, onClick }: {
  shortcut?: string;
  children: ReactNode;
  disabled?: boolean;
  onClick: () => void;
}) {
  return <Menu.Item disabled={disabled} onClick={onClick} rightSection={shortcut
    ? <span className="forge-menu-shortcut">{shortcut}</span>
    : undefined}>{children}</Menu.Item>;
}

import { app, BrowserWindow, Menu, type MenuItemConstructorOptions } from 'electron';

import type { EditorSnapshot, ForgeAction } from '../types/ForgeApi.js';

let editorSnapshot: EditorSnapshot | undefined;
let textInputActive = false;

export function installApplicationMenu(runAction: (action: ForgeAction) => void): void {
  const setup: MenuItemConstructorOptions = { label: 'Setup…', click: () => runAction('setup') };
  const settings: MenuItemConstructorOptions = {
    label: 'Settings…',
    accelerator: 'CmdOrCtrl+,',
    click: () => runAction('settings'),
  };
  const forgeMenu: MenuItemConstructorOptions[] = process.platform === 'darwin'
    ? [
      { role: 'about' },
      { type: 'separator' },
      setup,
      settings,
      { type: 'separator' },
      { role: 'services' },
      { type: 'separator' },
      { role: 'hide' },
      { role: 'hideOthers' },
      { role: 'unhide' },
      { type: 'separator' },
      { role: 'quit' },
    ]
    : [setup, settings];

  Menu.setApplicationMenu(Menu.buildFromTemplate([
    {
      label: 'File',
      submenu: [
        { label: 'New Project…', accelerator: 'CmdOrCtrl+N', click: () => runAction('newProject') },
        { label: 'Open Project…', accelerator: 'CmdOrCtrl+O', click: () => runAction('openProject') },
        { label: 'Save', accelerator: 'CmdOrCtrl+S', click: () => runAction('saveProject') },
        { label: 'Project Hub', click: () => runAction('projects') },
        { type: 'separator' },
        { role: process.platform === 'darwin' ? 'close' : 'quit' },
      ],
    },
    {
      label: 'Edit',
      submenu: [
        {
          id: 'editorUndo', label: 'Undo Project Change', accelerator: 'CmdOrCtrl+Z', enabled: false,
          click: (_item, window) => textInputActive && window instanceof BrowserWindow
            ? window.webContents.undo() : runAction('undoEditor'),
        },
        {
          id: 'editorRedo', label: 'Redo Project Change', accelerator: 'CmdOrCtrl+Y', enabled: false,
          click: (_item, window) => textInputActive && window instanceof BrowserWindow
            ? window.webContents.redo() : runAction('redoEditor'),
        },
        { type: 'separator' },
        { id: 'editorDuplicate', label: 'Duplicate Selected Entities', accelerator: 'CmdOrCtrl+D', enabled: false,
          click: () => runAction('duplicateEntities') },
        { id: 'editorDelete', label: 'Delete Selected Entities', accelerator: 'Delete', enabled: false,
          click: () => runAction('deleteEntities') },
        { type: 'separator' },
        { id: 'editorCut', label: 'Cut', accelerator: 'CmdOrCtrl+X', enabled: false,
          click: (_item, window) => { if (window instanceof BrowserWindow) window.webContents.cut(); } },
        { id: 'editorCopy', label: 'Copy Selected Entities', accelerator: 'CmdOrCtrl+C', enabled: false,
          click: (_item, window) => textInputActive && window instanceof BrowserWindow
            ? window.webContents.copy() : runAction('copyEntities') },
        { id: 'editorPaste', label: 'Paste Entities', accelerator: 'CmdOrCtrl+V', enabled: false,
          click: (_item, window) => textInputActive && window instanceof BrowserWindow
            ? window.webContents.paste() : runAction('pasteEntities') },
        { type: 'separator' },
        { id: 'editorSelectAll', label: 'Select All', accelerator: 'CmdOrCtrl+A', enabled: false,
          click: (_item, window) => { if (window instanceof BrowserWindow) window.webContents.selectAll(); } },
      ],
    },
    {
      label: 'View',
      submenu: [
        {
          label: 'Panels',
          submenu: [
            { label: 'Viewport', click: () => runAction('showViewport') },
            { label: 'Scene', click: () => runAction('showSceneTree') },
            { label: 'Properties', click: () => runAction('showProperties') },
            { label: 'Diagnostics', click: () => runAction('showDiagnostics') },
          ],
        },
        { label: 'Reset Layout', click: () => runAction('resetLayout') },
        { type: 'separator' },
        { role: 'resetZoom' },
        { role: 'zoomIn' },
        { role: 'zoomOut' },
        { type: 'separator' },
        { role: 'togglefullscreen' },
        { role: 'toggleDevTools', visible: !app.isPackaged },
      ],
    },
    { label: 'Window', submenu: [{ role: 'minimize' }, { role: 'zoom' }] },
    { label: 'Forge', submenu: forgeMenu },
  ]));
  updateEditorMenu();
}

export function setEditorMenuState(snapshot?: EditorSnapshot): void {
  editorSnapshot = snapshot;
  updateEditorMenu();
}

export function setEditorTextInputActive(active: boolean): void {
  textInputActive = active;
  updateEditorMenu();
}

export function isEditorTextInputActive(): boolean {
  return textInputActive;
}

function updateEditorMenu(): void {
  const menu = Menu.getApplicationMenu();
  const undo = menu?.getMenuItemById('editorUndo');
  const redo = menu?.getMenuItemById('editorRedo');
  const duplicate = menu?.getMenuItemById('editorDuplicate');
  const remove = menu?.getMenuItemById('editorDelete');
  const cut = menu?.getMenuItemById('editorCut');
  const copy = menu?.getMenuItemById('editorCopy');
  const paste = menu?.getMenuItemById('editorPaste');
  const selectAll = menu?.getMenuItemById('editorSelectAll');
  const selectedIds = new Set(editorSnapshot?.selection ?? []);
  const selected = editorSnapshot?.entities.filter((entity) => selectedIds.has(entity.id)) ?? [];
  const mutableSelection = selected.length > 0 && selected.every((entity) => !entity.state.locked);
  if (undo) {
    undo.label = textInputActive ? 'Undo' : 'Undo Project Change';
    undo.enabled = textInputActive || (editorSnapshot?.canUndo ?? false);
  }
  if (redo) {
    redo.label = textInputActive ? 'Redo' : 'Redo Project Change';
    redo.enabled = textInputActive || (editorSnapshot?.canRedo ?? false);
  }
  if (duplicate) duplicate.enabled = !textInputActive && mutableSelection;
  if (remove) remove.enabled = !textInputActive && mutableSelection;
  if (cut) cut.enabled = textInputActive;
  if (copy) {
    copy.label = textInputActive ? 'Copy' : 'Copy Selected Entities';
    copy.enabled = textInputActive || selected.length > 0;
  }
  if (paste) {
    paste.label = textInputActive ? 'Paste' : 'Paste Entities';
    paste.enabled = textInputActive || (editorSnapshot?.canPaste ?? false);
  }
  if (selectAll) selectAll.enabled = textInputActive;
}

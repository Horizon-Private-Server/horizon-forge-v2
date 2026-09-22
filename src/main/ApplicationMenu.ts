import { app, Menu, type MenuItemConstructorOptions } from 'electron';

import type { ForgeAction } from '../types/ForgeApi.js';

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
          click: () => runAction('undoEditor'),
        },
        {
          id: 'editorRedo', label: 'Redo Project Change', accelerator: 'CmdOrCtrl+Y', enabled: false,
          click: () => runAction('redoEditor'),
        },
        { type: 'separator' },
        { role: 'cut' },
        { role: 'copy' },
        { role: 'paste' },
        { role: 'selectAll' },
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
}

export function setEditorHistoryMenuState(canUndo = false, canRedo = false): void {
  const menu = Menu.getApplicationMenu();
  const undo = menu?.getMenuItemById('editorUndo');
  const redo = menu?.getMenuItemById('editorRedo');
  if (undo) undo.enabled = canUndo;
  if (redo) redo.enabled = canRedo;
}

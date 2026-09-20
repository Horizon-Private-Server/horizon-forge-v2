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
        { label: 'Project Hub', click: () => runAction('projects') },
        { type: 'separator' },
        { role: process.platform === 'darwin' ? 'close' : 'quit' },
      ],
    },
    {
      label: 'Edit',
      submenu: [
        { role: 'undo' },
        { role: 'redo' },
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

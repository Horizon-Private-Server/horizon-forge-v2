import { app, Menu, type MenuItemConstructorOptions } from 'electron';

import type { ForgeDialog } from '../types/ForgeApi.js';

export function installApplicationMenu(openDialog: (dialog: ForgeDialog) => void): void {
  const setup: MenuItemConstructorOptions = { label: 'Setup…', click: () => openDialog('setup') };
  const settings: MenuItemConstructorOptions = {
    label: 'Settings…',
    accelerator: 'CmdOrCtrl+,',
    click: () => openDialog('settings'),
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
    { label: 'File', submenu: [{ role: process.platform === 'darwin' ? 'close' : 'quit' }] },
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

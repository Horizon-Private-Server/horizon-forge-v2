import { ipcMain } from 'electron';

import type { UpdateLifecycleAction } from '../types/Updates.js';
import type { UpdateService } from './UpdateService.js';
import type { SettingsStore } from './Settings.js';

const lifecycleActions = new Set<UpdateLifecycleAction>([
  'download', 'cancel', 'retry', 'defer', 'install', 'open', 'dismiss',
]);

export function registerUpdateIpcHandlers(
  updates: UpdateService,
  settings: SettingsStore,
  assertSender: (senderId: number) => void,
): void {
  ipcMain.handle('forge:updates-check', async (event) => {
    assertSender(event.sender.id);
    const channel = (await settings.getSnapshot()).entries.find((entry) => entry.key === 'updates.channel')?.value;
    if (channel !== 'stable' && channel !== 'nightly') throw new Error('The update channel is invalid.');
    return updates.check(true, channel);
  });
  ipcMain.handle('forge:updates-lifecycle', (event) => {
    assertSender(event.sender.id);
    return updates.getLifecycleState();
  });
  ipcMain.handle('forge:updates-action', async (event, action: unknown) => {
    assertSender(event.sender.id);
    if (typeof action !== 'string' || !lifecycleActions.has(action as UpdateLifecycleAction)) {
      throw new TypeError('Update action is invalid.');
    }
    await updates.runUpdateAction(action as UpdateLifecycleAction);
  });
}

import { ipcMain } from 'electron';

import type { UpdateService } from './UpdateService.js';
import type { SettingsStore } from './Settings.js';

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
}

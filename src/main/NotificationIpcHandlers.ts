import { ipcMain } from 'electron';

import type { NotificationCenter } from './NotificationCenter.js';

export function registerNotificationIpcHandlers(
  notifications: NotificationCenter,
  assertSender: (senderId: number) => void,
): void {
  const notificationId = (value: unknown) => {
    if (typeof value !== 'string' || value.length === 0 || value.length > 200) {
      throw new TypeError('Notification ID is invalid.');
    }
    return value;
  };

  ipcMain.handle('forge:notifications-get', (event) => {
    assertSender(event.sender.id);
    return notifications.list();
  });
  ipcMain.handle('forge:notifications-dismiss', (event, id: unknown) => {
    assertSender(event.sender.id);
    notifications.dismiss(notificationId(id));
  });
  ipcMain.handle('forge:notifications-action', async (event, id: unknown) => {
    assertSender(event.sender.id);
    await notifications.runAction(notificationId(id));
  });
}

import type { BrowserWindow } from 'electron';

import type { ForgeNotification } from '../types/Notifications.js';

type NotificationAction = () => Promise<boolean | void>;

export class NotificationCenter {
  private readonly notifications = new Map<string, ForgeNotification>();
  private readonly actions = new Map<string, NotificationAction>();
  private readonly getMainWindow: () => BrowserWindow | undefined;

  constructor(getMainWindow: () => BrowserWindow | undefined) {
    this.getMainWindow = getMainWindow;
  }

  list(): ForgeNotification[] {
    return [...this.notifications.values()]
      .sort((left, right) => right.createdUnixMilliseconds - left.createdUnixMilliseconds);
  }

  publish(notification: ForgeNotification, action?: NotificationAction): void {
    this.notifications.set(notification.id, notification);
    if (action) this.actions.set(notification.id, action);
    else this.actions.delete(notification.id);
    this.changed();
  }

  dismiss(id: string): void {
    this.notifications.delete(id);
    this.actions.delete(id);
    this.changed();
  }

  async runAction(id: string): Promise<void> {
    const action = this.actions.get(id);
    if (!action) throw new Error('Notification action is unavailable.');
    if (await action() !== false) this.dismiss(id);
  }

  private changed(): void {
    this.getMainWindow()?.webContents.send('forge:notifications-changed', this.list());
  }
}

import type { BrowserWindow } from 'electron';

import type {
  UpdateLifecycleAction,
  UpdateLifecycleSnapshot,
} from '../types/Updates.js';
import type { NotificationCenter } from './NotificationCenter.js';

interface UpdateLifecycleCoordinatorOptions {
  notifications: NotificationCenter;
  getMainWindow(): BrowserWindow | undefined;
  runAction(action: UpdateLifecycleAction): Promise<void>;
}

const notificationId = 'update:lifecycle';

export class UpdateLifecycleCoordinator {
  private snapshot = idleUpdateLifecycle();
  private readonly options: UpdateLifecycleCoordinatorOptions;

  constructor(options: UpdateLifecycleCoordinatorOptions) {
    this.options = options;
  }

  get state(): UpdateLifecycleSnapshot {
    return clone(this.snapshot);
  }

  publish(snapshot: UpdateLifecycleSnapshot, notify = true): void {
    this.snapshot = clone(snapshot);
    this.options.getMainWindow()?.webContents.send('forge:updates-changed', this.state);
    if (!notify || snapshot.state === 'idle' || snapshot.state === 'checking' || snapshot.state === 'deferred') {
      if (snapshot.state === 'idle' || snapshot.state === 'deferred') this.options.notifications.dismiss(notificationId);
      return;
    }
    const primary = snapshot.actions.find(({ action }) => action !== 'defer' && action !== 'dismiss');
    this.options.notifications.publish({
      id: notificationId,
      title: snapshot.title,
      message: snapshot.message,
      severity: snapshot.severity,
      createdUnixMilliseconds: Date.now(),
      actionLabel: primary?.label,
    }, primary ? async () => {
      await this.options.runAction(primary.action);
      return false;
    } : undefined);
  }

  defer(): void {
    if (!this.snapshot.actions.some(({ action }) => action === 'defer')) {
      throw new Error('This update state cannot be deferred.');
    }
    this.publish({
      ...this.snapshot,
      state: 'deferred',
      title: 'Forge update deferred',
      message: `${this.snapshot.message}\n\nThe update will not install until you choose an action.`,
      actions: this.snapshot.actions.filter(({ action }) => action !== 'defer'),
    }, false);
  }

  clear(): void {
    this.publish(idleUpdateLifecycle(), false);
  }
}

export function idleUpdateLifecycle(): UpdateLifecycleSnapshot {
  return {
    state: 'idle',
    title: 'No Forge update is pending',
    message: 'Horizon Forge is not currently updating.',
    severity: 'info',
    actions: [],
  };
}

function clone(snapshot: UpdateLifecycleSnapshot): UpdateLifecycleSnapshot {
  return { ...snapshot, actions: snapshot.actions.map((action) => ({ ...action })) };
}

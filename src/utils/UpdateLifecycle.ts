import type { UpdateLifecycleSnapshot, UpdateLifecycleState } from '../types/Updates.js';

export interface UpdateLifecycleView {
  label: string;
  color: 'blue' | 'green' | 'red' | 'yellow';
  busy: boolean;
  progress?: number;
}

const labels: Record<Exclude<UpdateLifecycleState, 'idle'>, string> = {
  checking: 'Checking for updates',
  available: 'Forge update available',
  downloading: 'Downloading update',
  verifying: 'Verifying update',
  preparing: 'Preparing update',
  ready: 'Update ready',
  deferred: 'Update deferred',
  failed: 'Update failed',
  restarting: 'Restarting to update',
  'rolled-back': 'Update rolled back',
  updated: 'Forge updated',
};

export function describeUpdateLifecycle(snapshot: UpdateLifecycleSnapshot): UpdateLifecycleView | undefined {
  if (snapshot.state === 'idle') return undefined;
  const busy = ['checking', 'downloading', 'verifying', 'preparing', 'restarting'].includes(snapshot.state);
  const color = snapshot.state === 'failed' ? 'red'
    : snapshot.state === 'available' ? 'red'
      : snapshot.state === 'ready' || snapshot.state === 'updated' ? 'green'
        : snapshot.state === 'deferred' || snapshot.state === 'rolled-back' ? 'yellow' : 'blue';
  const showProgress = snapshot.state === 'downloading' || snapshot.state === 'verifying'
    || snapshot.state === 'preparing';
  const progress = showProgress && snapshot.total && snapshot.total > 0 && snapshot.completed !== undefined
    ? Math.max(0, Math.min(100, Math.floor((snapshot.completed / snapshot.total) * 100)))
    : undefined;
  return { label: labels[snapshot.state], color, busy, progress };
}

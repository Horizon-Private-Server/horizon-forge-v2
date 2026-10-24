import type { LogEntry, LogMessage, LogSeverity } from '../../types/Log.js';

export const LOG_ENTRY_LIMIT = 2000;

// Session state lives outside docked panels so closing a panel never loses messages.
export class LogStore {
  private entries: readonly LogEntry[] = [];
  private unread: readonly LogEntry[] = [];
  private sequence = 0;
  private readonly listeners = new Set<() => void>();
  private readonly snapshots = new Map<string, Set<string>>();

  getSnapshot = (): readonly LogEntry[] => this.entries;
  getUnreadSnapshot = (): readonly LogEntry[] => this.unread;

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  };

  write(source: string, severity: LogSeverity, message: string, code?: string): void {
    this.append([{ source, severity, message, code }]);
  }

  append(messages: readonly LogMessage[]): void {
    const timestamp = Date.now();
    const additions = messages.filter((value) => value.message.trim()).map((value) => ({
      ...value, id: ++this.sequence, timestamp,
    }));
    if (!additions.length) return;
    this.entries = [...this.entries, ...additions].slice(-LOG_ENTRY_LIMIT);
    this.unread = [...this.unread, ...additions].slice(-LOG_ENTRY_LIMIT);
    this.emit();
  }

  // Snapshots repeat during edits and progress updates; log only new or changed messages.
  sync(scope: string, messages: readonly LogMessage[]): void {
    const previous = this.snapshots.get(scope) ?? new Set<string>();
    const current = new Set<string>();
    const additions = messages.filter((value) => {
      const key = JSON.stringify([value.source, value.severity, value.code, value.message]);
      if (current.has(key)) return false;
      current.add(key);
      return !previous.has(key);
    });
    this.snapshots.set(scope, current);
    this.append(additions);
  }

  clear = (): void => {
    this.entries = [];
    this.unread = [];
    this.emit();
  };

  markRead = (): void => {
    if (!this.unread.length) return;
    this.unread = [];
    this.emit();
  };

  private emit(): void {
    for (const listener of this.listeners) listener();
  }
}

export const forgeLog = new LogStore();

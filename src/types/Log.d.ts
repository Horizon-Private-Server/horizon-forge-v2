export type LogSeverity = 'info' | 'success' | 'warning' | 'error';

export interface LogMessage {
  severity: LogSeverity;
  source: string;
  message: string;
  code?: string;
}

export interface LogEntry extends LogMessage {
  id: number;
  timestamp: number;
}

export interface LogNotificationActions {
  notifications: import('./Notifications.js').ForgeNotification[];
  dismiss(id: string): Promise<void>;
  runAction(id: string): Promise<void>;
}

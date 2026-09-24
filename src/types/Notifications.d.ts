export type NotificationSeverity = 'info' | 'warning' | 'error';

export interface ForgeNotification {
  id: string;
  title: string;
  message: string;
  severity: NotificationSeverity;
  createdUnixMilliseconds: number;
  actionLabel?: string;
}

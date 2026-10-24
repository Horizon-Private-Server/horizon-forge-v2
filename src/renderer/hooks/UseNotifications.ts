import { useCallback, useEffect, useState } from 'react';

import type { ForgeNotification } from '../../types/Notifications.js';
import { errorMessage } from '../../utils/Errors.ts';

import { forgeLog } from '../log/LogStore.ts';

export function useNotifications() {
  const [notifications, setNotifications] = useState<ForgeNotification[]>([]);

  useEffect(() => {
    let mounted = true;
    let receivedEvent = false;
    const receive = (value: ForgeNotification[]) => {
      setNotifications(value);
      forgeLog.sync('Notifications', value.map((entry) => ({ source: entry.title,
        severity: entry.severity, message: entry.message })));
    };
    const unsubscribe = window.forge.onNotificationsChanged((value) => {
      receivedEvent = true;
      receive(value);
    });
    void window.forge.getNotifications().then((value) => {
      if (mounted && !receivedEvent) receive(value);
    }).catch((cause) => { if (mounted) forgeLog.write('Notifications', 'error', errorMessage(cause)); });
    return () => {
      mounted = false;
      unsubscribe();
    };
  }, []);

  const dismiss = useCallback((id: string) => window.forge.dismissNotification(id), []);
  const runAction = useCallback((id: string) => window.forge.runNotificationAction(id), []);
  return { notifications, dismiss, runAction };
}

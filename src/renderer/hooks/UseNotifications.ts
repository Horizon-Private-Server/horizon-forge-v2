import { useCallback, useEffect, useState } from 'react';

import type { ForgeNotification } from '../../types/Notifications.js';

export function useNotifications() {
  const [notifications, setNotifications] = useState<ForgeNotification[]>([]);

  useEffect(() => {
    let mounted = true;
    const unsubscribe = window.forge.onNotificationsChanged(setNotifications);
    void window.forge.getNotifications().then((value) => {
      if (mounted) setNotifications(value);
    }).catch(() => undefined);
    return () => {
      mounted = false;
      unsubscribe();
    };
  }, []);

  const dismiss = useCallback((id: string) => window.forge.dismissNotification(id), []);
  const runAction = useCallback((id: string) => window.forge.runNotificationAction(id), []);
  return { notifications, dismiss, runAction };
}

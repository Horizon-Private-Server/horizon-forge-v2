import { createContext } from 'react';

import type { LogNotificationActions } from '../../types/Log.js';

export const LogNotificationsContext = createContext<LogNotificationActions | undefined>(undefined);

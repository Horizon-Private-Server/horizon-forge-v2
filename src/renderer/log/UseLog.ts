import { useCallback, useEffect, useState, useSyncExternalStore } from 'react';

import type { LogSeverity } from '../../types/Log.js';
import { forgeLog } from './LogStore.ts';

export function useLog() {
  return useSyncExternalStore(forgeLog.subscribe, forgeLog.getSnapshot);
}

export function useLogReporter(source: string, severity: LogSeverity = 'error') {
  return useCallback((message?: string) => {
    if (message) forgeLog.write(source, severity, message);
  }, [source, severity]);
}

// Dialogs retain local validation while recording it in the same session history.
export function useLoggedMessage(source: string, severity: LogSeverity = 'error') {
  const [message, setMessage] = useState<string>();
  const report = useLogReporter(source, severity);
  const update = useCallback((value?: string) => {
    setMessage(value);
    report(value);
  }, [report]);
  return [message, update] as const;
}

export function useLogStatus(source: string, message?: string, severity: LogSeverity = 'warning') {
  useEffect(() => {
    forgeLog.sync(source, message ? [{ source, severity, message }] : []);
  }, [message, severity, source]);
}

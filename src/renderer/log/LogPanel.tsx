import { Button, Checkbox, Group, MultiSelect, Text, TextInput } from '@mantine/core';
import type { DockviewPanelApi } from 'dockview-react';
import { useContext, useEffect, useMemo, useRef, useState } from 'react';

import type { LogSeverity } from '../../types/Log.js';
import { errorMessage } from '../../utils/Errors.ts';
import { forgeLog, LOG_ENTRY_LIMIT } from './LogStore.ts';
import { LogNotificationsContext } from './LogNotificationsContext.ts';
import { useLog } from './UseLog.ts';

const MESSAGE_TYPES: LogSeverity[] = ['info', 'success', 'warning', 'error'];

export function LogPanel({ api, visible: isVisible = true }: { api?: DockviewPanelApi; visible?: boolean }) {
  const entries = useLog();
  const notifications = useContext(LogNotificationsContext);
  const [pendingAction, setPendingAction] = useState<string>();
  const [filter, setFilter] = useState('');
  const [severities, setSeverities] = useState<string[]>(MESSAGE_TYPES);
  const [follow, setFollow] = useState(true);
  const [panelVisible, setPanelVisible] = useState(api?.isVisible ?? true);
  const scroll = useRef<HTMLDivElement>(null);
  const visible = useMemo(() => {
    const query = filter.trim().toLocaleLowerCase();
    return entries.filter((entry) => severities.includes(entry.severity)
      && (!query || `${entry.source} ${entry.code ?? ''} ${entry.message}`.toLocaleLowerCase().includes(query)));
  }, [entries, filter, severities]);

  useEffect(() => {
    if (!api) return;
    setPanelVisible(api.isVisible);
    const subscription = api.onDidVisibilityChange((event) => setPanelVisible(event.isVisible));
    return () => subscription.dispose();
  }, [api]);

  useEffect(() => {
    if (isVisible && panelVisible) forgeLog.markRead();
  }, [entries, panelVisible, isVisible]);

  useEffect(() => {
    if (follow && scroll.current) scroll.current.scrollTop = scroll.current.scrollHeight;
  }, [follow, visible]);

  const run = (action: () => Promise<unknown>) => {
    void action().catch((cause) => forgeLog.write('Log', 'error', errorMessage(cause)));
  };
  return <section aria-label="Log" className="forge-log">
    <Group className="forge-log-toolbar" gap="xs">
      <TextInput aria-label="Filter log" placeholder="Filter log…" value={filter}
        onChange={(event) => setFilter(event.currentTarget.value)} />
      <MultiSelect aria-label="Message types" value={severities} onChange={setSeverities}
        data={MESSAGE_TYPES.map((value) => ({ value, label: severityLabel(value) }))} />
      <Checkbox label="Follow output" checked={follow} onChange={(event) => setFollow(event.currentTarget.checked)} />
      <Button variant="default" disabled={!visible.length} onClick={() => run(() => navigator.clipboard.writeText(
        visible.map((entry) => `${new Date(entry.timestamp).toISOString()} [${entry.severity.toUpperCase()}] `
          + `[${entry.source}] ${entry.code ? `${entry.code}: ` : ''}${entry.message}`).join('\n'),
      ))}>Copy</Button>
      <Button variant="default" disabled={!entries.length} onClick={forgeLog.clear}>Clear</Button>
      <Button variant="subtle" onClick={() => run(() => window.forge.revealLogs())}>Open detailed logs</Button>
    </Group>
    {notifications && notifications.notifications.length > 0 && <div className="forge-log-actions" aria-label="Pending actions">
      {notifications.notifications.map((notification) => <Group key={notification.id} gap="xs">
        <Text size="xs">{notification.title}</Text>
        {notification.actionLabel && <Button size="compact-xs" loading={pendingAction === notification.id}
          disabled={pendingAction !== undefined} onClick={() => {
            setPendingAction(notification.id);
            run(() => notifications.runAction(notification.id).finally(() => setPendingAction(undefined)));
          }}>{notification.actionLabel}</Button>}
        <Button size="compact-xs" variant="subtle" aria-label={`Dismiss ${notification.title}`}
          onClick={() => run(() => notifications.dismiss(notification.id))}>Dismiss</Button>
      </Group>)}
    </div>}
    <div className="forge-log-output" ref={scroll} role="log" aria-label="Session messages"
      aria-live="polite" aria-relevant="additions" tabIndex={0} onScroll={(event) => {
        const element = event.currentTarget;
        setFollow(element.scrollHeight - element.scrollTop - element.clientHeight < 24);
      }}>
      {!visible.length && <Text c="dimmed" p="sm">{entries.length ? 'No matching messages.' : 'No messages yet.'}</Text>}
      {visible.map((entry) => <div className="forge-log-entry" data-severity={entry.severity} key={entry.id}>
        <time dateTime={new Date(entry.timestamp).toISOString()}>{new Date(entry.timestamp).toLocaleTimeString()}</time>
        <span className="forge-log-level">{severityLabel(entry.severity)}</span>
        <span className="forge-log-message"><span className="forge-log-source">[{entry.source}] </span>
          {entry.code && <span>{entry.code}: </span>}{entry.message}</span>
      </div>)}
    </div>
    <Text className="forge-log-count" size="xs" c="dimmed">{visible.length} of {entries.length} messages · Session history, latest {LOG_ENTRY_LIMIT.toLocaleString()}</Text>
  </section>;
}

function severityLabel(severity: LogSeverity): string {
  return severity.charAt(0).toUpperCase() + severity.slice(1);
}

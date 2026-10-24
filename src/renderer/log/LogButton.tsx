import { TerminalWindowIcon } from '@phosphor-icons/react/dist/csr/TerminalWindow';
import { Button, Tooltip } from '@mantine/core';
import { useSyncExternalStore } from 'react';

import { forgeLog } from './LogStore.ts';

export function LogButton({ onOpen }: { onOpen(): void }) {
  const unread = useSyncExternalStore(forgeLog.subscribe, forgeLog.getUnreadSnapshot);
  const errors = unread.filter((entry) => entry.severity === 'error').length;
  const warnings = unread.filter((entry) => entry.severity === 'warning').length;
  const label = `Show Log · ${unread.length} new messages · ${errors} errors · ${warnings} warnings`;
  return <Tooltip label={label}>
    <Button aria-label={label} variant="subtle" size="compact-xs" leftSection={<TerminalWindowIcon />}
      color={errors ? 'red' : warnings ? 'yellow' : 'gray'} onClick={onOpen}>
      Log{unread.length ? ` (${unread.length})` : ''}</Button>
  </Tooltip>;
}

import { ArrowClockwiseIcon } from '@phosphor-icons/react/dist/csr/ArrowClockwise';
import { CheckCircleIcon } from '@phosphor-icons/react/dist/csr/CheckCircle';
import { CircleNotchIcon } from '@phosphor-icons/react/dist/csr/CircleNotch';
import { DownloadIcon } from '@phosphor-icons/react/dist/csr/Download';
import { PauseIcon } from '@phosphor-icons/react/dist/csr/Pause';
import { WarningIcon } from '@phosphor-icons/react/dist/csr/Warning';
import { Badge, Button, Group, Menu, Progress, Stack, Text, UnstyledButton } from '@mantine/core';
import { useState } from 'react';

import type { UpdateLifecycleAction, UpdateLifecycleSnapshot } from '../../types/Updates.js';
import { errorMessage } from '../../utils/Errors.ts';
import { formatBytes } from '../../utils/Format.ts';
import { describeUpdateLifecycle } from '../../utils/UpdateLifecycle.ts';

interface UpdateStatusMenuProps {
  snapshot?: UpdateLifecycleSnapshot;
  onAction(action: UpdateLifecycleAction): Promise<void>;
}

export function UpdateStatusMenu({ snapshot, onAction }: UpdateStatusMenuProps) {
  const [pending, setPending] = useState<UpdateLifecycleAction>();
  const [error, setError] = useState<string>();
  const view = snapshot ? describeUpdateLifecycle(snapshot) : undefined;
  if (!snapshot || !view) return null;

  const details = [snapshot.version && `Version ${snapshot.version}`, snapshot.channel, snapshot.source]
    .filter(Boolean).join(' · ');
  const run = (action: UpdateLifecycleAction) => {
    setPending(action);
    setError(undefined);
    void onAction(action).catch((reason) => setError(errorMessage(reason))).finally(() => setPending(undefined));
  };

  return <>
    <span className="visually-hidden" aria-live="polite">
      {view.label}. {snapshot.title}{view.progress === undefined ? '' : `. ${view.progress}%`}
    </span>
    <Menu position="bottom-end" width={420} withinPortal={false} closeOnItemClick={false}>
      <Menu.Target>
        <UnstyledButton
          className="update-status-trigger"
          aria-label={`${view.label}. ${snapshot.title}`}
          aria-busy={view.busy || pending !== undefined}
          title={[snapshot.title, details].filter(Boolean).join('\n')}
        >
          <Badge
            className="update-status-badge"
            color={view.color}
            leftSection={<StatusIcon state={snapshot.state} busy={view.busy} />}
            variant="light"
          >
            <span className="update-status-label">{view.label}</span>
            {view.progress !== undefined && <span aria-hidden="true"> · {view.progress}%</span>}
          </Badge>
        </UnstyledButton>
      </Menu.Target>
      <Menu.Dropdown className="update-status-menu">
        <Stack gap="xs" p="xs">
          <div>
            <Text fw={600} size="sm">{snapshot.title}</Text>
            {details && <Text c="dimmed" size="xs">{details}</Text>}
          </div>
          <Text className="update-status-message" size="xs">{snapshot.message}</Text>
          {view.progress !== undefined && <div>
            <Progress value={view.progress} animated={view.busy} aria-label={`Update progress ${view.progress}%`} />
            <Text c="dimmed" mt={2} size="xs">{formatProgress(snapshot)}</Text>
          </div>}
          {error && <Text c="red" role="alert" size="xs">{error}</Text>}
          {snapshot.actions.length > 0 && <Group gap="xs">
            {snapshot.actions.map((item, index) => <Button
              key={item.action}
              size="compact-xs"
              variant={index === 0 ? 'filled' : 'default'}
              disabled={pending !== undefined}
              loading={pending === item.action}
              onClick={() => run(item.action)}
            >
              {item.label}
            </Button>)}
          </Group>}
        </Stack>
      </Menu.Dropdown>
    </Menu>
  </>;
}

function StatusIcon({ state, busy }: Pick<UpdateLifecycleSnapshot, 'state'> & { busy: boolean }) {
  if (busy) return <CircleNotchIcon className="update-status-spinner" weight="bold" />;
  if (state === 'available') return <DownloadIcon weight="bold" />;
  if (state === 'current' || state === 'ready' || state === 'updated') return <CheckCircleIcon weight="bold" />;
  if (state === 'deferred') return <PauseIcon weight="bold" />;
  if (state === 'failed' || state === 'rolled-back') return <WarningIcon weight="bold" />;
  return <ArrowClockwiseIcon weight="bold" />;
}

function formatProgress(snapshot: UpdateLifecycleSnapshot): string {
  if (snapshot.completed === undefined || snapshot.total === undefined) return '';
  if (snapshot.progressUnit === 'bytes') return `${formatBytes(snapshot.completed)} of ${formatBytes(snapshot.total)}`;
  return `${snapshot.completed} of ${snapshot.total} files`;
}

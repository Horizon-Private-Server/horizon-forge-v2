import { BellIcon } from '@phosphor-icons/react/dist/csr/Bell';
import { XIcon } from '@phosphor-icons/react/dist/csr/X';
import { ActionIcon, Button, Group, Menu, Stack, Text } from '@mantine/core';
import { useState } from 'react';

import type { ForgeNotification } from '../../types/Notifications.js';
import { errorMessage } from '../../utils/Errors.ts';

interface NotificationMenuProps {
  notifications: ForgeNotification[];
  onDismiss(id: string): Promise<void>;
  onAction(id: string): Promise<void>;
}

export function NotificationMenu({ notifications, onDismiss, onAction }: NotificationMenuProps) {
  const [error, setError] = useState<string>();
  return <Menu position="bottom-end" width={380} withinPortal={false} closeOnItemClick={false}>
    <Menu.Target>
      <span className="notification-trigger">
        <ActionIcon variant="subtle" color="gray" aria-label="Notifications">
          <BellIcon size={17} weight={notifications.length ? 'fill' : 'regular'} />
        </ActionIcon>
        {notifications.length > 0 && <span className="notification-dot" aria-hidden="true" />}
      </span>
    </Menu.Target>
    <Menu.Dropdown className="notification-menu">
      <Text fw={600} size="xs" px="xs" py={4}>Notifications</Text>
      {error && <Text c="red" size="xs" px="xs" pb="xs">{error}</Text>}
      {notifications.length === 0
        ? <Text c="dimmed" size="xs" px="xs" py="sm">No notifications</Text>
        : <Stack gap={0}>
          {notifications.map((notification) => <article
            className="notification-item"
            data-severity={notification.severity}
            key={notification.id}
          >
            <Group justify="space-between" gap="xs" wrap="nowrap">
              <Text fw={600} size="xs">{notification.title}</Text>
              <ActionIcon
                variant="subtle"
                color="gray"
                size="xs"
                aria-label={`Dismiss ${notification.title}`}
                onClick={() => void onDismiss(notification.id).catch((reason) => setError(errorMessage(reason)))}
              >
                <XIcon size={12} />
              </ActionIcon>
            </Group>
            <Text className="notification-message" c="dimmed" size="xs">{notification.message}</Text>
            {notification.actionLabel && <Button
              mt="xs"
              size="compact-xs"
              onClick={() => void onAction(notification.id).catch((reason) => setError(errorMessage(reason)))}
            >
              {notification.actionLabel}
            </Button>}
          </article>)}
        </Stack>}
    </Menu.Dropdown>
  </Menu>;
}

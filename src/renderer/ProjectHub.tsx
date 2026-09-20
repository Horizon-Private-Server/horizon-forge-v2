import {
  Alert,
  Badge,
  Button,
  Checkbox,
  Group,
  Modal,
  Paper,
  Select,
  Stack,
  Table,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useCallback, useEffect, useState } from 'react';

import type { ForgeHostStatus, ForgeProjectDescriptor, ProjectHubState, UyaProjectPreflight } from '../types/ForgeApi.js';
import { formatBytes } from '../utils/Format.ts';
import { CatalogMaintenanceModal } from './CatalogMaintenanceModal.tsx';
import { MissingAssetsModal } from './MissingAssetsModal.tsx';

interface ProjectHubProps {
  hostStatus?: ForgeHostStatus;
  requestedAction?: { id: number; action: 'newProject' | 'openProject' };
  refreshToken: number;
  onOpen(project: ForgeProjectDescriptor): void;
  onOpenSetup(): void;
}

export function ProjectHub({ hostStatus, requestedAction, refreshToken, onOpen, onOpenSetup }: ProjectHubProps) {
  const [hub, setHub] = useState<ProjectHubState>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [createOpened, setCreateOpened] = useState(false);
  const [renameProject, setRenameProject] = useState<ForgeProjectDescriptor>();
  const [name, setName] = useState('');
  const [level, setLevel] = useState<string | null>(null);
  const [allowPartial, setAllowPartial] = useState(false);
  const [preflight, setPreflight] = useState<UyaProjectPreflight>();
  const [preflightBusy, setPreflightBusy] = useState(false);
  const [rename, setRename] = useState('');
  const [recoveryProject, setRecoveryProject] = useState<ForgeProjectDescriptor>();
  const [recoveryId, setRecoveryId] = useState<string | null>(null);

  const offerProject = useCallback((project: ForgeProjectDescriptor) => {
    if (project.recoveries.length === 0 && !project.migrationPending) {
      onOpen(project);
      return;
    }
    setRecoveryProject(project);
    setRecoveryId(project.recoveries[0]?.id ?? null);
  }, [onOpen]);

  const refresh = useCallback(async () => {
    try {
      setError(undefined);
      setHub(await window.forge.getProjectHub());
    } catch (cause) {
      setError(message(cause));
    }
  }, []);

  const openPicker = useCallback(async () => {
    try {
      setBusy(true);
      setError(undefined);
      const project = await window.forge.openForgeProject();
      if (project) offerProject(project);
    } catch (cause) {
      setError(message(cause));
    } finally {
      setBusy(false);
    }
  }, [offerProject]);

  useEffect(() => { void refresh(); }, [refresh, refreshToken]);
  useEffect(() => {
    if (requestedAction?.action === 'newProject') setCreateOpened(true);
    else if (requestedAction?.action === 'openProject') void openPicker();
  }, [requestedAction, openPicker]);

  useEffect(() => {
    if (level === null) return;
    let current = true;
    setPreflightBusy(true);
    void window.forge.preflightUyaProject(Number(level))
      .then((value) => { if (current) setPreflight(value); })
      .catch((cause) => { if (current) setError(message(cause)); })
      .finally(() => { if (current) setPreflightBusy(false); });
    return () => { current = false; };
  }, [level]);

  async function createProject() {
    if (!name.trim() || level === null) return;
    try {
      setBusy(true);
      setError(undefined);
      const project = await window.forge.createUyaProject(name.trim(), Number(level), allowPartial);
      if (project) {
        setCreateOpened(false);
        offerProject(project);
      }
    } catch (cause) {
      setError(message(cause));
    } finally {
      setBusy(false);
    }
  }

  async function openRecent(path: string) {
    try {
      setBusy(true);
      setError(undefined);
      offerProject(await window.forge.openRecentProject(path));
    } catch (cause) {
      setError(message(cause));
    } finally {
      setBusy(false);
    }
  }

  async function saveRename() {
    if (!renameProject || !rename.trim()) return;
    try {
      setBusy(true);
      setError(undefined);
      await window.forge.renameForgeProject(renameProject.path, rename.trim());
      setRenameProject(undefined);
      await refresh();
    } catch (cause) {
      setError(message(cause));
    } finally {
      setBusy(false);
    }
  }

  async function restoreRecovery() {
    if (!recoveryProject || !recoveryId) return;
    try {
      setBusy(true);
      setError(undefined);
      const project = await window.forge.restoreForgeProject(recoveryProject.path, recoveryId);
      setRecoveryProject(undefined);
      setRecoveryId(null);
      onOpen(project);
    } catch (cause) {
      setError(message(cause));
    } finally {
      setBusy(false);
    }
  }

  async function openSavedProject() {
    if (!recoveryProject) return;
    try {
      setBusy(true);
      setError(undefined);
      const project = recoveryProject.migrationPending
        ? await window.forge.migrateForgeProject(recoveryProject.path)
        : recoveryProject;
      setRecoveryProject(undefined);
      setRecoveryId(null);
      onOpen(project);
    } catch (cause) {
      setError(message(cause));
    } finally {
      setBusy(false);
    }
  }

  const warnings = preflight?.warnings ?? hub?.creation?.warnings ?? [];
  const canCreate = Boolean(hub?.creation?.levels.length)
    && Boolean(hostStatus?.capabilities.includes('uya.projects.base.mobys'));
  const selectedRecovery = recoveryProject?.recoveries.find((recovery) => recovery.id === recoveryId);

  return (
    <section className="project-hub">
      <Group justify="space-between" align="flex-start">
        <div>
          <Title order={3}>Projects</Title>
          <Text c="dimmed" size="sm">Create a UYA project or continue a recent one.</Text>
        </div>
        <Group>
          <CatalogMaintenanceModal onChanged={() => void refresh()} />
          <Button variant="default" onClick={() => void openPicker()} loading={busy}>Open…</Button>
          <Button onClick={() => setCreateOpened(true)} disabled={!canCreate}>New project</Button>
        </Group>
      </Group>

      {(error || hub?.diagnostic) && <Alert color="red" title="Project hub">{error ?? hub?.diagnostic}</Alert>}

      <Paper withBorder className="project-list">
        <Table highlightOnHover verticalSpacing="xs">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Project</Table.Th>
              <Table.Th>Target</Table.Th>
              <Table.Th>Base</Table.Th>
              <Table.Th>Modified</Table.Th>
              <Table.Th>Assets</Table.Th>
              <Table.Th aria-label="Actions" />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {hub?.recentProjects.map((recent) => {
              const project = recent.project;
              return (
                <Table.Tr key={recent.path}>
                  <Table.Td>
                    <Text fw={600} size="sm">{project?.name ?? fileName(recent.path)}</Text>
                    <Text c="dimmed" size="xs" className="project-path">{recent.path}</Text>
                    {recent.error && <Text c="red" size="xs">{recent.error}</Text>}
                  </Table.Td>
                  <Table.Td>{project ? `${project.targetGame} ${project.targetRegion}` : '—'}</Table.Td>
                  <Table.Td>{project ? `Level ${project.baseLevel}` : '—'}</Table.Td>
                  <Table.Td>{project ? new Date(project.modifiedUnixMilliseconds).toLocaleString() : '—'}</Table.Td>
                  <Table.Td>
                    {project && <Badge color={project.missingAssetCount ? 'red' : 'teal'} variant="light">
                      {project.missingAssetCount ? `${project.missingAssetCount} missing` : 'Ready'}
                    </Badge>}
                    {project && project.recoveries.length > 0 && <Badge color="yellow" variant="light" ml="xs">
                      Recovery
                    </Badge>}
                    {project?.migrationPending && <Badge color="yellow" variant="light" ml="xs">Upgrade</Badge>}
                  </Table.Td>
                  <Table.Td>
                    <Group justify="flex-end" wrap="nowrap">
                      {project?.missingAssetCount ? <MissingAssetsModal
                        project={project}
                        onOpenSetup={onOpenSetup}
                        onRepaired={() => void refresh()}
                      /> : null}
                      <Button variant="subtle" onClick={() => void openRecent(recent.path)} disabled={!project || busy}>Open</Button>
                      <Button variant="subtle" onClick={() => {
                        if (!project) return;
                        setRenameProject(project);
                        setRename(project.name);
                      }} disabled={!project || project.migrationPending || busy}>Rename</Button>
                      <Button variant="subtle" onClick={() => void window.forge.revealForgeProject(recent.path)}>Reveal</Button>
                      <Button variant="subtle" color="gray" onClick={async () => {
                        try { setHub(await window.forge.removeRecentProject(recent.path)); }
                        catch (cause) { setError(message(cause)); }
                      }}>Remove</Button>
                    </Group>
                  </Table.Td>
                </Table.Tr>
              );
            })}
            {hub && hub.recentProjects.length === 0 && (
              <Table.Tr><Table.Td colSpan={6}><Text c="dimmed" ta="center">No recent projects.</Text></Table.Td></Table.Tr>
            )}
          </Table.Tbody>
        </Table>
      </Paper>

      <Text c="dimmed" size="xs">Default location: {hub?.projectsDirectory ?? 'Loading…'}</Text>

      <Modal opened={createOpened} onClose={() => setCreateOpened(false)} title="Create UYA project">
        <Stack>
          <TextInput label="Project name" value={name} onChange={(event) => setName(event.currentTarget.value)} autoFocus />
          <Select
            label="Base level"
            placeholder="Select a level"
            data={(hub?.creation?.levels ?? []).map((value) => ({ value: String(value), label: `Level ${value}` }))}
            value={level}
            onChange={(value) => {
              setLevel(value);
              setPreflight(undefined);
              setAllowPartial(false);
            }}
            searchable
          />
          {preflightBusy && <Text c="dimmed" size="xs">Checking source instances and global assets…</Text>}
          {preflight && (
            <Text size="xs">
              {preflight.renderableInstanceCount} renderable and {preflight.modelLessInstanceCount} model-less moby instances
              {preflight.missingAssetInstanceCount
                ? `; ${preflight.missingAssetInstanceCount} instances across ${preflight.missingClassCount} class IDs are missing assets.`
                : `; all ${preflight.sourceInstanceCount} instances will be included.`}
            </Text>
          )}
          {warnings.length > 0 && (
            <Alert color="yellow" title="Current base import coverage">
              <Stack>{warnings.map((warning) => <Text size="xs" key={warning}>{warning}</Text>)}</Stack>
            </Alert>
          )}
          {warnings.length > 0 && (
            <Checkbox
              checked={allowPartial}
              onChange={(event) => setAllowPartial(event.currentTarget.checked)}
              label="Create with the currently supported base content"
            />
          )}
          <Text c="dimmed" size="xs">You will choose the project directory after clicking Create.</Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setCreateOpened(false)}>Cancel</Button>
            <Button
              onClick={() => void createProject()}
              loading={busy}
              disabled={!name.trim() || level === null || !preflight || preflightBusy || (warnings.length > 0 && !allowPartial)}
            >Create</Button>
          </Group>
        </Stack>
      </Modal>

      <Modal opened={Boolean(renameProject)} onClose={() => setRenameProject(undefined)} title="Rename project">
        <Stack>
          <TextInput label="Project name" value={rename} onChange={(event) => setRename(event.currentTarget.value)} autoFocus />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setRenameProject(undefined)}>Cancel</Button>
            <Button onClick={() => void saveRename()} loading={busy} disabled={!rename.trim()}>Rename</Button>
          </Group>
        </Stack>
      </Modal>

      <Modal
        opened={Boolean(recoveryProject)}
        onClose={() => setRecoveryProject(undefined)}
        title={recoveryProject?.recoveries.length ? 'Unsaved work is available' : 'Project upgrade required'}
      >
        <Stack>
          <Alert color="yellow">
            {recoveryProject?.recoveries.length
              ? 'Forge found autosaved work newer than the last explicit save. Review it before choosing which version to open.'
              : 'This project uses the version-zero format. Forge will preserve the existing files until you explicitly upgrade it.'}
          </Alert>
          {Boolean(recoveryProject?.recoveries.length) && <Select
            label="Recovery snapshot"
            value={recoveryId}
            onChange={setRecoveryId}
            data={(recoveryProject?.recoveries ?? []).map((recovery) => ({
              value: recovery.id,
              label: `${new Date(recovery.createdUnixMilliseconds).toLocaleString()} · ${recovery.entityCount} entities`,
            }))}
          />}
          {selectedRecovery && (
            <Paper withBorder p="sm">
              <Text size="sm" fw={600}>{selectedRecovery.name}</Text>
              <Text size="xs" c="dimmed">{selectedRecovery.entityCount} entities · {formatBytes(selectedRecovery.size)}</Text>
            </Paper>
          )}
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setRecoveryProject(undefined)}>Cancel</Button>
            {Boolean(recoveryProject?.recoveries.length) && (
              <Button variant="default" onClick={() => void openSavedProject()} loading={busy}>
                {recoveryProject?.migrationPending ? 'Upgrade saved project' : 'Open saved project'}
              </Button>
            )}
            {recoveryProject?.recoveries.length
              ? <Button onClick={() => void restoreRecovery()} loading={busy} disabled={!recoveryId}>Recover autosave</Button>
              : <Button onClick={() => void openSavedProject()} loading={busy}>Upgrade and open</Button>}
          </Group>
        </Stack>
      </Modal>
    </section>
  );
}

function fileName(value: string): string {
  return value.split(/[\\/]/).filter(Boolean).at(-1) ?? value;
}

function message(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

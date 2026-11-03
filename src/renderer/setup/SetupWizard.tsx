import { Alert, Badge, Button, Checkbox, Group, Modal, Paper, Progress, Stack, Text, TextInput, Title } from '@mantine/core';
import { useEffect, useRef, useState } from 'react';

import type { AssetSourceGame, SetupProgress, SetupState } from '../../types/ForgeApi.js';
import { errorMessage } from '../../utils/Errors.ts';
import { formatBytes } from '../../utils/Format.ts';

import { forgeLog } from '../log/LogStore.ts';
import { useLoggedMessage } from '../log/UseLog.ts';

interface SetupWizardProps {
  opened: boolean;
  onClose(): void;
}

export function SetupWizard({ opened, onClose }: SetupWizardProps) {
  const [state, setState] = useState<SetupState>();
  const [progress, setProgress] = useState<SetupProgress>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useLoggedMessage('Setup');
  const active = useRef(false);

  useEffect(() => window.forge.onSetupProgress((value) => {
    setProgress(value);
    forgeLog.sync('Setup progress', [{ source: 'Setup', severity: 'info', message: progressLabel(value) }]);
  }), []);
  useEffect(() => {
    if (opened) void refresh().catch(showError);
  }, [opened]);

  async function refresh(): Promise<SetupState> {
    const next = await window.forge.getSetupState();
    setState(next);
    return next;
  }

  function showError(reason: unknown): void {
    setError(errorMessage(reason));
  }

  async function runOperation(action: () => Promise<void>): Promise<void> {
    if (active.current) return;
    active.current = true;
    setBusy(true);
    setError(undefined);
    try {
      await action();
    } catch (reason) {
      showError(reason);
    } finally {
      try { await refresh(); } catch (reason) { showError(reason); }
      active.current = false;
      setBusy(false);
      setProgress(undefined);
    }
  }

  async function chooseSource(game: AssetSourceGame): Promise<void> {
    await runOperation(async () => {
      setProgress({ operation: 'validate', game, completed: 0, total: 1 });
      const result = await window.forge.chooseAssetSource(game);
      if (result && !result.identity.isSupported) setError(result.identity.diagnostic);
    });
  }

  async function importSource(game: AssetSourceGame, force = false): Promise<void> {
    setProgress({ operation: 'import', game, completed: 0, total: 1 });
    const result = await window.forge.importAssets(game, force);
    forgeLog.write('Setup', result.failedAssets ? 'warning' : 'success',
      `${force ? 'Re-imported' : 'Imported'} ${result.assetAppearances.toLocaleString()} ${game} asset appearances `
      + `across ${result.completedLevels} levels. ${result.failedAssets} assets failed.`);
  }

  async function finishSetup(): Promise<void> {
    await runOperation(async () => {
      let next = await refresh();
      if (!next.developmentIsoReady) {
        setProgress({ operation: 'copy', game: 'UYA', completed: 0, total: 1 });
        if (!await window.forge.createDevelopmentIso()) return;
        next = await refresh();
      }
      if (next.sources.UYA.importEnabled && !next.sources.UYA.importComplete)
        await importSource('UYA');
      if (!(await refresh()).required) {
        forgeLog.write('Setup', 'success', 'UYA setup complete.');
        onClose();
      }
    });
  }

  const insufficientSpace = Boolean(!state?.developmentIsoReady && state?.sources.UYA.identity?.size && state.availableBytes !== undefined
    && state.availableBytes < state.sources.UYA.identity.size);
  const canFinish = Boolean(state?.sources.UYA.identity?.fingerprint
    && (state.developmentIsoReady || state.developmentIsoDirectory) && !insufficientSpace && !busy);
  const setupRequired = state?.required ?? true;
  const canClose = !setupRequired && !busy;

  return (
    <Modal
      opened={opened}
      onClose={canClose ? onClose : () => {}}
      withCloseButton={canClose}
      closeOnClickOutside={canClose}
      closeOnEscape={canClose}
      title="Set up Horizon Forge"
      size="lg"
    >
      <Stack>
        <Text c="dimmed">Choose a clean source for each game to import reusable assets. Interrupted imports resume automatically.</Text>
        {error && <Alert color="red" title="Setup operation failed">{error}</Alert>}

        <Title order={5}>Projects</Title>
        <PathRow label="Projects directory" value={state?.projectsDirectory ?? ''} disabled={busy || !state}
          onChoose={() => void runOperation(async () => { await window.forge.chooseSetupDirectory('projects'); })} />

        {(['UYA', 'DL'] as const).map((game) => {
          const source = state?.sources[game];
          const status = !source?.iso ? 'Not configured' : !source.identity ? 'Source unavailable or unverified'
            : source.importComplete ? 'Assets imported' : 'Ready to import / resume';
          return <Paper withBorder p="sm" key={game}>
            <Stack gap="xs">
              <Group justify="space-between">
                <Title order={5}>{game === 'UYA' ? 'Up Your Arsenal (UYA)' : 'Deadlocked (DL)'}</Title>
                <Badge variant="light" color={source?.importComplete ? 'teal' : 'gray'}>{status}</Badge>
              </Group>
              <Text size="sm" c="dimmed">{game === 'UYA'
                ? 'Source for UYA map projects and reusable assets.'
                : 'Optional Deadlocked assets for the shared catalog.'}</Text>
              <PathRow label={`Clean ${game} NTSC-U ISO`} value={source?.iso ?? ''}
                onChoose={() => void chooseSource(game)} disabled={busy || !state} />
              {source?.identity && <Text size="xs">
                {source.identity.region} · revision {source.identity.revision} · {source.identity.serial} · {formatBytes(source.identity.size)} · MD5 {source.identity.fingerprint}
              </Text>}
              <Checkbox
                label={`Enable ${game} asset import`}
                description={game === 'UYA' ? 'Included when finishing UYA setup.' : 'Import separately below; you can finish setup without DL assets.'}
                checked={source?.importEnabled ?? false}
                disabled={busy || !state}
                onChange={(event) => {
                  const enabled = event.currentTarget.checked;
                  void runOperation(async () => { await window.forge.setSetting(`imports.${game.toLowerCase()}.enabled`, enabled); });
                }}
              />
              <Group justify="flex-end">
                <Button variant="default" disabled={busy || !source?.identity || !source.importEnabled}
                  onClick={() => void runOperation(() => importSource(game, source?.importComplete))}>
                  {source?.importComplete ? `Re-import ${game} assets` : `Import / resume ${game} assets`}
                </Button>
              </Group>
            </Stack>
          </Paper>;
        })}

        <Paper withBorder p="sm">
          <Stack gap="xs">
            <Title order={5}>UYA map building</Title>
            <Text size="sm" c="dimmed">Forge creates a separate UYA development ISO for patching. Clean source ISOs stay read-only.</Text>
            <PathRow label="Development ISO directory" value={state?.developmentIsoDirectory ?? ''} disabled={busy || !state}
              onChoose={() => void runOperation(async () => { await window.forge.chooseSetupDirectory('developmentIsos'); })} />
            {state?.developmentIsoReady ? <Text size="xs">Development ISO ready: {state.developmentIso}</Text>
              : state?.sources.UYA.identity?.size ? <Text size="xs" c={insufficientSpace ? 'red' : 'dimmed'}>
                Copy requires {formatBytes(state.sources.UYA.identity.size)}; destination has {formatBytes(state.availableBytes)} available.
              </Text> : null}
          </Stack>
        </Paper>

        {progress && <Stack gap={2}>
          <Text size="xs" role="status">{progressLabel(progress)}</Text>
          <Progress value={progress.total > 0 ? Math.min(100, Math.max(0, progress.completed / progress.total * 100)) : 0} animated />
        </Stack>}

        <Group justify="flex-end">
          {busy && <Button variant="default" onClick={() => void window.forge.cancelSetupOperation().catch(showError)}>Cancel operation</Button>}
          <Button disabled={setupRequired ? !canFinish : busy} onClick={() => setupRequired ? void finishSetup() : onClose()}>
            {setupRequired ? 'Finish UYA setup' : 'Done'}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

function progressLabel(progress: SetupProgress): string {
  const game = progress.game ?? 'UYA';
  return {
    validate: `Verifying clean ${game} ISO…`,
    copy: 'Creating and verifying UYA development ISO…',
    import: `Importing reusable ${game} assets…`,
  }[progress.operation];
}

function PathRow({ label, value, onChoose, disabled }: { label: string; value: string; onChoose(): void; disabled: boolean }) {
  return (
    <div className="setting-item">
      <TextInput label={label} value={value} readOnly />
      <Button variant="default" aria-label={`Choose ${label}`} onClick={onChoose} disabled={disabled}>Browse…</Button>
    </div>
  );
}

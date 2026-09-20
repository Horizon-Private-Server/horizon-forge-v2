import { Alert, Button, Checkbox, Group, Modal, Progress, Stack, Text, TextInput, Title } from '@mantine/core';
import { useEffect, useState } from 'react';

import type { SetupProgress, SetupState } from '../../types/ForgeApi.js';
import { errorMessage } from '../../utils/Errors.ts';
import { formatBytes } from '../../utils/Format.ts';

interface SetupWizardProps {
  opened: boolean;
  onClose(): void;
}

export function SetupWizard({ opened, onClose }: SetupWizardProps) {
  const [state, setState] = useState<SetupState>();
  const [progress, setProgress] = useState<SetupProgress>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [notice, setNotice] = useState<string>();

  useEffect(() => window.forge.onSetupProgress(setProgress), []);
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

  async function chooseDirectory(kind: 'projects' | 'developmentIsos'): Promise<void> {
    try {
      setState(await window.forge.chooseSetupDirectory(kind));
    } catch (reason) {
      showError(reason);
    }
  }

  async function chooseSource(): Promise<void> {
    setBusy(true);
    setError(undefined);
    setProgress({ operation: 'validate', completed: 0, total: 1 });
    try {
      const result = await window.forge.chooseUyaSource();
      if (result && !result.identity.isSupported) setError(result.identity.diagnostic);
      await refresh();
    } catch (reason) {
      showError(reason);
    } finally {
      setBusy(false);
      setProgress(undefined);
    }
  }

  async function finishSetup(): Promise<void> {
    setBusy(true);
    setError(undefined);
    try {
      let next = await refresh();
      if (!next.developmentIsoReady) {
        setProgress({ operation: 'copy', completed: 0, total: 1 });
        if (!await window.forge.createDevelopmentIso()) return;
        next = await refresh();
      }
      if (next.importUyaAssets && !next.assetImportComplete) {
        setProgress({ operation: 'import', completed: 0, total: 1 });
        await window.forge.importUyaAssets();
      }
      if (!(await refresh()).required) onClose();
    } catch (reason) {
      showError(reason);
    } finally {
      setBusy(false);
      setProgress(undefined);
    }
  }

  async function reimportAssets(): Promise<void> {
    setBusy(true);
    setError(undefined);
    setNotice(undefined);
    setProgress({ operation: 'import', completed: 0, total: 1 });
    try {
      const result = await window.forge.importUyaAssets(true);
      setNotice(`Re-imported ${result.assetAppearances.toLocaleString()} asset appearances across ${result.completedLevels} levels.`);
      await refresh();
    } catch (reason) {
      showError(reason);
    } finally {
      setBusy(false);
      setProgress(undefined);
    }
  }

  const insufficientSpace = Boolean(!state?.developmentIsoReady && state?.source?.size && state.availableBytes !== undefined
    && state.availableBytes < state.source.size);
  const canFinish = Boolean(state?.source?.fingerprint && state.developmentIsoDirectory && !insufficientSpace && !busy);
  const setupRequired = state?.required ?? true;

  return (
    <Modal
      opened={opened}
      onClose={setupRequired ? () => {} : onClose}
      withCloseButton={!setupRequired}
      closeOnClickOutside={!setupRequired}
      closeOnEscape={!setupRequired}
      title="Set up Horizon Forge"
      size="lg"
    >
      <Stack>
        <Text c="dimmed">Forge keeps the clean source read-only and creates a separate development ISO for patching.</Text>
        {error && <Alert color="red" title="Setup could not continue">{error}</Alert>}
        {notice && <Alert color="teal" title="Asset import complete">{notice}</Alert>}

        <Title order={5}>Locations</Title>
        <PathRow label="Projects directory" value={state?.projectsDirectory ?? ''} onChoose={() => void chooseDirectory('projects')} disabled={busy} />
        <PathRow label="Development ISO directory" value={state?.developmentIsoDirectory ?? ''} onChoose={() => void chooseDirectory('developmentIsos')} disabled={busy} />

        <Title order={5}>Clean UYA source</Title>
        <PathRow label="NTSC-U ISO" value={state?.sourceIso ?? ''} onChoose={() => void chooseSource()} disabled={busy} />
        {state?.source && (
          <Text size="xs">
            {state.source.game} · {state.source.region} · revision {state.source.revision} · {state.source.serial} · {formatBytes(state.source.size)} · MD5 {state.source.fingerprint}
          </Text>
        )}

        <Checkbox
          label="Import reusable UYA assets after setup"
          description="The asset import runs in the next setup stage and can be resumed."
          checked={state?.importUyaAssets ?? true}
          disabled={busy}
          onChange={(event) => void window.forge.setSetting('imports.uya.enabled', event.currentTarget.checked)
            .then(() => refresh()).catch(showError)}
        />

        {state?.source?.size && (
          <Text size="xs" c={insufficientSpace ? 'red' : 'dimmed'}>
            Copy requires {formatBytes(state.source.size)}; destination has {formatBytes(state.availableBytes)} available.
          </Text>
        )}
        {progress && (
          <Stack gap={2}>
            <Text size="xs">{{
              validate: 'Verifying clean ISO…',
              copy: 'Creating and verifying development ISO…',
              import: 'Importing reusable UYA assets…',
            }[progress.operation]}</Text>
            <Progress value={progress.completed / progress.total * 100} animated />
          </Stack>
        )}

        <Group justify="flex-end">
          {busy && <Button variant="default" onClick={() => void window.forge.cancelSetupOperation()}>Cancel operation</Button>}
          {state?.assetImportComplete && !busy && (
            <Button variant="default" onClick={() => void reimportAssets()}>Re-import UYA assets</Button>
          )}
          <Button disabled={!canFinish} onClick={() => void finishSetup()}>
            {state?.developmentIsoReady && state.importUyaAssets && !state.assetImportComplete
              ? 'Resume asset import'
              : 'Finish setup'}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

function PathRow({ label, value, onChoose, disabled }: { label: string; value: string; onChoose(): void; disabled: boolean }) {
  return (
    <div className="setting-item">
      <TextInput label={label} value={value} readOnly />
      <Button variant="default" onClick={onChoose} disabled={disabled}>Browse…</Button>
    </div>
  );
}

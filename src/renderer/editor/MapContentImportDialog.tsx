import {
  Accordion, Alert, Badge, Button, Code, Group, Modal, Progress, ScrollArea, Select, Stack, Text,
} from '@mantine/core';
import { useEffect, useMemo, useRef, useState } from 'react';

import type {
  ContentImportOptions, ContentImportPlan, ContentImportReferenceChoice, ContentImportSelection,
} from '../../types/ContentImport.js';
import type { EditorSnapshot, Progress as ForgeProgress } from '../../types/ForgeApi.js';
import { editorEntityDisplayName } from '../../utils/EntityDisplay.ts';
import { createContentImportQualificationRecord } from '../../utils/ContentImportQualification.ts';
import { errorMessage } from '../../utils/Errors.ts';
import { formatBytes } from '../../utils/Format.ts';
import { ContentImportFamilyBoard } from './ContentImportFamilyBoard.tsx';

import { forgeLog } from '../log/LogStore.ts';
import { useLoggedMessage } from '../log/UseLog.ts';

interface MapContentImportDialogProps {
  opened: boolean;
  project: EditorSnapshot;
  onClose(): void;
  onApplied(snapshot: EditorSnapshot): void;
}

export function MapContentImportDialog({ opened, project, onClose, onApplied }: MapContentImportDialogProps) {
  const [options, setOptions] = useState<ContentImportOptions>();
  const [sourceLevel, setSourceLevel] = useState<string | null>(null);
  const [selections, setSelections] = useState<Record<string, 'add' | 'replace'>>({});
  const [plan, setPlan] = useState<ContentImportPlan>();
  const [plannedSignature, setPlannedSignature] = useState('');
  const [progress, setProgress] = useState<ForgeProgress>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useLoggedMessage('Map content import');
  const [showPreview, setShowPreview] = useState(false);
  const [applying, setApplying] = useState(false);
  const [referenceTargets, setReferenceTargets] = useState<Record<string, string>>({});
  const [qualificationCopied, setQualificationCopied] = useState(false);
  const [qualificationRecord, setQualificationRecord] = useState<string>();
  const requestVersion = useRef(0);

  const selectionList = useMemo(() => Object.entries(selections)
    .map(([family, mode]) => ({ family, mode }))
    .sort((left, right) => left.family.localeCompare(right.family)), [selections]);
  const referenceChoiceList: ContentImportReferenceChoice[] = useMemo(
    () => (plan?.referenceRequirements ?? []).flatMap((requirement) => {
      const value = referenceTargets[referenceRequirementKey(requirement)];
      if (!value) return [];
      return [{
        ownerSourceIndex: requirement.ownerSourceIndex,
        fieldKey: requirement.fieldKey,
        sourceValue: requirement.sourceValue,
        action: value === 'clear' ? 'clear' : 'map',
        ...(value === 'clear' ? {} : { targetEntityId: value }),
      }];
    }),
    [plan, referenceTargets],
  );
  const projectEntitiesById = useMemo(
    () => new Map(project.entities.map((entity) => [entity.id, entity])),
    [project.entities],
  );
  const signature = JSON.stringify([sourceLevel, selectionList]);
  const reportCurrent = Boolean(plan) && signature === plannedSignature;

  useEffect(() => {
    if (!opened || !reportCurrent || !plan) return;
    forgeLog.sync('Map import plan', [
      ...plan.blockers.map((message) => ({ source: 'Map import plan', severity: 'error' as const, message })),
      ...plan.warnings.map((message) => ({ source: 'Map import plan', severity: 'warning' as const, message })),
    ]);
  }, [opened, plan, reportCurrent]);

  useEffect(() => window.forge.onContentImportProgress(setProgress), []);
  useEffect(() => {
    if (!opened) return;
    const version = ++requestVersion.current;
    setOptions(undefined);
    setSourceLevel(null);
    setSelections({});
    setPlan(undefined);
    setPlannedSignature('');
    setProgress(undefined);
    setError(undefined);
    setShowPreview(false);
    setApplying(false);
    setReferenceTargets({});
    setQualificationCopied(false);
    void window.forge.getContentImportOptions()
      .then((value) => { if (requestVersion.current === version) setOptions(value); })
      .catch((cause) => { if (requestVersion.current === version) setError(errorMessage(cause)); });
  }, [opened, project.projectId]);
  useEffect(() => {
    let active = true;
    setQualificationRecord(undefined);
    if (plan) void createContentImportQualificationRecord(plan, referenceChoiceList)
      .then((value) => { if (active) setQualificationRecord(value); })
      .catch((cause) => {
        if (active) setError(`Unable to create qualification record: ${errorMessage(cause)}`);
      });
    return () => { active = false; };
  }, [plan, referenceChoiceList]);

  async function preview(level = sourceLevel, useDefaults = false, openPreview = false) {
    if (level === null) return;
    setBusy(true);
    setError(undefined);
    setProgress({ completed: 0, total: 1 });
    const version = ++requestVersion.current;
    const requestedSelections: ContentImportSelection[] = useDefaults ? [] : selectionList;
    try {
      const next = await window.forge.planContentImport(Number(level), requestedSelections);
      if (requestVersion.current !== version) return;
      setPlan(next);
      setReferenceTargets({});
      setQualificationCopied(false);
      const nextSelections = Object.fromEntries(next.families
        .filter((family) => family.selected)
        .map((family) => [family.family, family.mode === 'replace' ? 'replace' : 'add'])) as
        Record<string, 'add' | 'replace'>;
      setSelections(nextSelections);
      setPlannedSignature(JSON.stringify([level, Object.entries(nextSelections)
        .map(([family, mode]) => ({ family, mode }))
        .sort((left, right) => left.family.localeCompare(right.family))]));
      setShowPreview(openPreview);
    } catch (cause) {
      if (requestVersion.current === version) setError(errorMessage(cause));
    } finally {
      if (requestVersion.current === version) {
        setBusy(false);
        setProgress(undefined);
      }
    }
  }

  function close() {
    requestVersion.current += 1;
    if (busy) void window.forge.cancelContentImport();
    setBusy(false);
    setProgress(undefined);
    onClose();
  }

  async function apply() {
    if (!plan || sourceLevel === null || !reportCurrent || plan.blockers.length > 0
      || !referencesComplete) return;
    setBusy(true);
    setApplying(true);
    setError(undefined);
    setProgress({ completed: 0, total: 1 });
    const version = ++requestVersion.current;
    try {
      const snapshot = await window.forge.applyContentImport(
        Number(sourceLevel), plan.batchId, selectionList, referenceChoiceList);
      if (requestVersion.current !== version) return;
      setBusy(false);
      setApplying(false);
      setProgress(undefined);
      forgeLog.write('Map content import', 'success', `Imported content from level ${sourceLevel}. Batch ${plan.batchId}.`);
      onApplied(snapshot);
      onClose();
    } catch (cause) {
      if (requestVersion.current === version) setError(errorMessage(cause));
    } finally {
      if (requestVersion.current === version) {
        setBusy(false);
        setApplying(false);
        setProgress(undefined);
      }
    }
  }

  async function copyQualificationRecord() {
    if (!qualificationRecord) return;
    try {
      await navigator.clipboard.writeText(qualificationRecord);
      setQualificationCopied(true);
    } catch (cause) {
      setError(`Unable to copy qualification record: ${errorMessage(cause)}`);
    }
  }

  const selectedCount = Object.keys(selections).length;
  const referencesComplete = referenceChoiceList.length === (plan?.referenceRequirements.length ?? 0);
  const progressValue = progress && progress.total > 0 ? progress.completed / progress.total * 100 : 0;
  const totals = plan?.families.filter((family) => family.selected).reduce((current, family) => ({
    add: current.add + family.add.length,
    reuse: current.reuse + family.reuse.length,
    remap: current.remap + family.remap.length,
    remove: current.remove + family.remove.length,
    retain: current.retain + family.protect.length,
  }), { add: 0, reuse: 0, remap: 0, remove: 0, retain: 0 });

  return <Modal opened={opened} onClose={close} title={showPreview ? 'Preview map content' : 'Import map content'} size="xl">
    <Stack>
      <Text size="sm">{showPreview
        ? `Review the planned changes to ${project.projectName}.`
        : `Choose what to bring into ${project.projectName} from another ${project.target.game} level.`}</Text>
      {error && <Alert color="red" title="Map content import failed">{error}</Alert>}
      {!showPreview && options?.warnings.map((warning) => <Alert color="yellow" key={warning}>{warning}</Alert>)}
      {!showPreview && <Select
        autoFocus
        searchable
        label="Donor level"
        placeholder={options ? 'Select a level' : 'Loading levels…'}
        disabled={!options || busy}
        data={(options?.levels ?? []).map((level) => ({ value: String(level.value), label: level.label }))}
        value={sourceLevel}
        onChange={(value) => {
          setSourceLevel(value);
          setPlan(undefined);
          setSelections({});
          setPlannedSignature('');
          setShowPreview(false);
          if (value !== null) void preview(value, true);
        }}
      />}
      {busy && <Stack gap={4} aria-live="polite">
        <Text size="xs" c="dimmed">{applying
          ? 'Importing content and validating the updated project…'
          : 'Reading donor level and computing dependency-safe changes…'}</Text>
        <Progress value={progressValue} animated />
      </Stack>}
      {plan && !showPreview && <>
        <ContentImportFamilyBoard
          busy={busy}
          families={plan.families}
          selections={selections}
          onChange={setSelections}
        />
        {!reportCurrent && <Text c="dimmed" size="xs">Selections changed. Preview to recalculate the changes.</Text>}
      </>}
      {plan && showPreview && totals && <PreviewReport
        plan={plan}
        projectEntitiesById={projectEntitiesById}
        referenceTargets={referenceTargets}
        totals={totals}
        onReferenceTarget={(key, value) => {
          setQualificationCopied(false);
          setReferenceTargets((current) => ({ ...current, [key]: value }));
        }}
      />}
      <Group justify="space-between">
        <Text c="dimmed" size="xs">
          {showPreview ? 'Preview only. No project changes have been applied yet.'
            : plan ? `${selectedCount} content families selected.`
            : 'Choose a donor level to inspect its supported content.'}
        </Text>
        <Group>
          {showPreview && <Button variant="default" onClick={() => setShowPreview(false)}>Back</Button>}
          {showPreview && <Button
            aria-label="Copy qualification record"
            disabled={busy || !qualificationRecord}
            variant="default"
            onClick={() => void copyQualificationRecord()}
          >{qualificationCopied ? 'Record copied' : 'Copy record'}</Button>}
          <Button variant="default" onClick={close}>{busy ? 'Cancel' : 'Close'}</Button>
          {!showPreview && <Button
            loading={busy}
            disabled={sourceLevel === null || (Boolean(plan) && selectedCount === 0)}
            onClick={() => reportCurrent ? setShowPreview(true) : void preview(sourceLevel, false, true)}
          >{reportCurrent ? 'View preview' : 'Preview changes'}</Button>}
          {showPreview && <Button
            loading={busy && applying}
            disabled={!reportCurrent || plan?.blockers.length !== 0 || !referencesComplete}
            onClick={() => void apply()}
          >Import</Button>}
        </Group>
      </Group>
    </Stack>
  </Modal>;
}

function PreviewReport({ plan, projectEntitiesById, referenceTargets, totals, onReferenceTarget }: {
  plan: ContentImportPlan;
  projectEntitiesById: ReadonlyMap<string, EditorSnapshot['entities'][number]>;
  referenceTargets: Record<string, string>;
  totals: { add: number; reuse: number; remap: number; remove: number; retain: number };
  onReferenceTarget(key: string, value: string): void;
}) {
  const familyLabels = useMemo(
    () => new Map(plan.families.map((family) => [family.family, family.label])),
    [plan.families],
  );
  return <Stack>
    <Alert aria-live="polite" color={plan.blockers.length ? 'red' : 'green'}
      title={plan.blockers.length ? 'Preview blocked' : 'Preview ready'}>
      <Text size="xs">
        {totals.add} add, {totals.reuse} reuse, {totals.remap} remap, {totals.remove} remove,
        {' '}{totals.retain} protected · {formatBytes(plan.estimatedBytes)}
      </Text>
      {plan.blockers.map((blocker) => <Text size="xs" key={blocker}>{blocker}</Text>)}
    </Alert>
    {totals.retain > 0 && <Alert color="yellow" title="Protected target content will remain">
      <Text size="xs">
        Replace keeps {totals.retain} target items required by surviving references. Expand a family below for details.
      </Text>
    </Alert>}
    {plan.warnings.length > 0 && <Alert color="yellow" title="Plan warnings">
      {plan.warnings.map((warning) => <Text size="xs" key={warning}>{warning}</Text>)}
    </Alert>}
    {plan.referenceRequirements.length > 0 && <Stack gap="xs">
      <Text fw={600} size="sm">Reference mapping</Text>
      <Text c="dimmed" size="xs">Choose a retained target for each donor link, or clear nullable links.</Text>
      {plan.referenceRequirements.map((requirement) => {
        const key = referenceRequirementKey(requirement);
        const owner = requirement.family === 'mobys'
          ? `Moby #${requirement.ownerSourceIndex}`
          : requirement.family === 'areas'
            ? `Area #${requirement.ownerSourceIndex}`
            : familyLabels.get(requirement.family) ?? requirement.family;
        const candidates = requirement.candidateEntityIds.flatMap((entityId) => {
          const entity = projectEntitiesById.get(entityId);
          return entity ? [{
            value: entityId,
            label: `${editorEntityDisplayName(entity)} · ${entity.id.slice(0, 8)}`,
          }] : [];
        });
        return <Group key={key} justify="space-between" wrap="nowrap">
          <Text size="xs">
            {owner} · <Code>{requirement.fieldKey}</Code>
            {' '}→ {requirement.targetKind}
          </Text>
          <Select
            aria-label={`${owner} ${requirement.fieldKey} target`}
            allowDeselect={false}
            data={[
              ...(requirement.nullable ? [{ value: 'clear', label: 'Clear reference' }] : []),
              ...candidates,
            ]}
            placeholder="Choose target"
            searchable
            size="xs"
            value={referenceTargets[key] ?? null}
            w={260}
            onChange={(value) => { if (value) onReferenceTarget(key, value); }}
          />
        </Group>;
      })}
    </Stack>}
    <ScrollArea.Autosize mah="52vh" type="auto">
      <Accordion multiple variant="separated">
        {plan.families.filter((family) => family.selected).map((family) => {
          const changes = [
            ['Add', family.add],
            ['Reuse', family.reuse],
            ['Remap', family.remap],
            ['Remove', family.remove],
            ['Protected', family.protect],
          ] as const;
          const itemCount = changes.reduce((count, [, values]) => count + values.length, 0);
          const items = changes.flatMap(([label, values]) => values.slice(0, 25)
            .map((value) => `${label}: ${value}`)).slice(0, 25);
          return <Accordion.Item key={family.family} value={family.family}>
            <Accordion.Control>
              <Group justify="space-between" wrap="nowrap">
                <Text fw={600}>{family.label}</Text>
                <Group gap="xs">
                  <Badge variant="outline">{family.mode}</Badge>
                  <Badge variant="light">{itemCount} items</Badge>
                </Group>
              </Group>
            </Accordion.Control>
            <Accordion.Panel>
              <Stack gap={4}>
                {family.blockers.map((value) => <Alert color="red" key={value}>{value}</Alert>)}
                {family.warnings.map((value) => <Alert color="yellow" key={value}>{value}</Alert>)}
                {itemCount === 0 && <Text c="dimmed" size="xs">No item changes.</Text>}
                {items.map((value, index) => <Code block key={`${index}:${value}`}>{value}</Code>)}
                {itemCount > items.length
                  && <Text c="dimmed" size="xs">{itemCount - items.length} more items omitted.</Text>}
              </Stack>
            </Accordion.Panel>
          </Accordion.Item>;
        })}
      </Accordion>
    </ScrollArea.Autosize>
  </Stack>;
}

function referenceRequirementKey(requirement: {
  family: string; ownerSourceIndex: number; fieldKey: string; sourceValue: number;
}): string {
  return JSON.stringify([
    requirement.family, requirement.ownerSourceIndex, requirement.fieldKey, requirement.sourceValue,
  ]);
}


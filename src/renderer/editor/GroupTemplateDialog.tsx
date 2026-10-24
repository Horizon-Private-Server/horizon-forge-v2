import { Button, Code, Group, Modal, NumberInput, Select, Stack, Text } from '@mantine/core';
import { useEffect, useMemo, useState } from 'react';

import type {
  EditorEntity,
  EditorGroup,
  EditorGroupTemplateDefinition,
  EditorGroupTemplatePreview,
} from '../../types/EditorRuntime.js';
import { errorMessage } from '../../utils/Errors.ts';
import { entityTreeKind, entityTreeText } from '../../utils/SceneSelection.ts';
import { useEditor } from './EditorContext.ts';

import { forgeLog } from '../log/LogStore.ts';
import { useLoggedMessage } from '../log/UseLog.ts';

export function GroupTemplateDialog({ group, onClose }: {
  group?: EditorGroup;
  onClose(): void;
}) {
  const {
    project, busy, previewGroupTemplate, cancelGroupTemplatePreview, applyGroupTemplatePreview,
  } = useEditor();
  const initialTemplateId = group?.template?.templateId ?? project.groupTemplates[0]?.templateId;
  const [templateId, setTemplateId] = useState(initialTemplateId);
  const definition = project.groupTemplates.find((value) => value.templateId === templateId);
  const [parameters, setParameters] = useState<Record<string, number>>(() =>
    parameterValues(definition, group));
  const [preview, setPreview] = useState<EditorGroupTemplatePreview>();
  const [pending, setPending] = useState(false);
  const [error, setError] = useLoggedMessage('Group template');
  const seedId = group?.template?.seedEntityId
    ?? (project.selection.length === 1 ? project.selection[0] : undefined);
  const seed = project.entities.find((entity) => entity.id === seedId);
  const validSeed = seed && entityTreeKind(seed) === definition?.seedKind ? seed : undefined;
  const entities = useMemo(() => new Map(project.entities.map((entity) => [entity.id, entity])), [project.entities]);

  useEffect(() => {
    setParameters(parameterValues(definition, group));
    setPreview(undefined);
    setError(undefined);
  }, [definition, group]);

  useEffect(() => {
    forgeLog.sync('Group template preview', (preview?.diagnostics ?? [])
      .map((value) => ({ ...value, source: 'Group template' })));
  }, [preview]);

  const close = () => {
    void cancelGroupTemplatePreview();
    onClose();
  };
  const generatePreview = async () => {
    if (!definition || !validSeed) return;
    setPending(true);
    setError(undefined);
    setPreview(undefined);
    try {
      await cancelGroupTemplatePreview();
      setPreview(await previewGroupTemplate({
        templateId: definition.templateId,
        templateVersion: definition.templateVersion,
        parameters: definition.parameters.map((parameter) => ({
          key: parameter.key,
          value: parameters[parameter.key] ?? parameter.defaultValue,
        })),
        groupId: group?.id,
        seedEntityId: validSeed.id,
      }));
    }
    catch (cause) { setError(errorMessage(cause)); }
    finally { setPending(false); }
  };
  const apply = async () => {
    if (!preview) return;
    setPending(true);
    if (await applyGroupTemplatePreview(preview.token)) onClose();
    else setPending(false);
  };

  return <Modal opened onClose={close} title={group ? `Regenerate ${group.name}` : 'Generate group'} size="lg">
    <Stack gap="sm">
      <Select
        disabled={Boolean(group)}
        label="Template"
        data={project.groupTemplates.map((value) => ({ value: value.templateId, label: value.label }))}
        value={templateId}
        onChange={(value) => { if (value) setTemplateId(value); }}
      />
      {definition && <Text c="dimmed" size="xs">{definition.description}</Text>}
      <Text size="sm">
        Seed: {validSeed ? entityTreeText(validSeed) : `Select one ${definition?.seedKind ?? 'compatible'} first.`}
      </Text>
      {definition?.parameters.map((parameter) => <NumberInput
        key={parameter.key}
        label={parameter.label}
        min={parameter.minimum}
        max={parameter.maximum}
        step={parameter.step}
        value={parameters[parameter.key] ?? parameter.defaultValue}
        onChange={(value) => {
          const next = typeof value === 'number' ? value : Number(value);
          if (!Number.isFinite(next)) return;
          setParameters((current) => ({ ...current, [parameter.key]: next }));
          setPreview(undefined);
          void cancelGroupTemplatePreview();
        }}
      />)}
      {error && <Text c="red" size="sm">{error}</Text>}
      {preview && <TemplatePreview preview={preview} entities={entities} />}
      <Group justify="flex-end">
        <Button variant="default" onClick={close}>Cancel</Button>
        <Button disabled={busy || pending || !definition || !validSeed} variant="default"
          onClick={() => void generatePreview()}>Preview</Button>
        <Button disabled={busy || pending || !preview} onClick={() => void apply()}>
          {group ? 'Regenerate' : 'Create group'}
        </Button>
      </Group>
    </Stack>
  </Modal>;
}

function TemplatePreview({ preview, entities }: {
  preview: EditorGroupTemplatePreview;
  entities: ReadonlyMap<string, EditorEntity>;
}) {
  return <Stack gap="xs">
    <Text fw={600} size="sm">{preview.suggestedName}</Text>
    <Text size="xs">
      {preview.members.length} members · +{preview.addedMembers.length} / −{preview.removedMembers.length}
      {preview.ambiguousCandidateCount ? ` · ${preview.ambiguousCandidateCount} ambiguous` : ''}
    </Text>
    {(preview.manualAdditions.length > 0 || preview.manualRemovals.length > 0) && <Text c="dimmed" size="xs">
      Preserving {preview.manualAdditions.length} manual addition(s) and {preview.manualRemovals.length} manual removal(s).
    </Text>}
    {preview.candidates.length < preview.candidateCount && <Text c="dimmed" size="xs">
      Showing {preview.candidates.length} of {preview.candidateCount} candidate details, with ambiguous matches first.
    </Text>}
    <Stack gap={4} mah={240} style={{ overflowY: 'auto' }}>
      {preview.candidates.map((candidate) => <Text key={candidate.entityId} size="xs"
        c={candidate.disposition === 'ambiguous' ? 'yellow' : undefined}>
        <Code>{candidate.disposition === 'ambiguous' ? 'Review' : 'Include'}</Code>{' '}
        {`${entities.has(candidate.entityId)
          ? entityTreeText(entities.get(candidate.entityId)!)
          : candidate.entityId}:`} {candidate.evidence.join(' ')}
      </Text>)}
    </Stack>
    {preview.diagnostics.map((diagnostic) => <Text key={`${diagnostic.code}:${diagnostic.message}`}
      c={diagnostic.severity === 'error' ? 'red' : diagnostic.severity === 'warning' ? 'yellow' : 'dimmed'}
      size="xs">{diagnostic.message}</Text>)}
  </Stack>;
}

function parameterValues(
  definition: EditorGroupTemplateDefinition | undefined,
  group: EditorGroup | undefined,
): Record<string, number> {
  const stored = new Map(group?.template?.parameters.map((value) => [value.key, value.value]));
  return Object.fromEntries(definition?.parameters.map((parameter) =>
    [parameter.key, stored.get(parameter.key) ?? parameter.defaultValue]) ?? []);
}

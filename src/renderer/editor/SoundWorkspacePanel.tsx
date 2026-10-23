import {
  Alert, Badge, Button, Checkbox, Group, Loader, Menu, SegmentedControl, Slider, Stack, Switch, Text, TextInput,
} from '@mantine/core';
import { useEffect, useMemo, useRef, useState } from 'react';

import type { SoundProgramEntry, SoundWorkspace } from '../../types/SoundWorkspace.js';
import { errorMessage } from '../../utils/Errors.ts';
import { formatBytes } from '../../utils/Format.ts';
import {
  readSoundPreviewPreferences, type SoundPreviewPreferences,
} from '../../utils/SoundPreviewPreferences.ts';
import { virtualGridWindow } from '../../utils/VirtualGrid.ts';
import { useEditor } from './EditorContext.ts';
import {
  SoundPreviewRuntime, type SoundPreviewPlaybackState,
} from './SoundPreviewRuntime.ts';
import { useVirtualGridViewport } from './TexturePanelHooks.ts';

const CARD_MIN_WIDTH = 340;
const GRID_ROW_HEIGHT = 112;
const EMPTY_PLAYBACK: SoundPreviewPlaybackState = {
  playing: false, loading: false, position: 0, duration: 0,
};
const DEFAULT_PREFERENCES: SoundPreviewPreferences = {
  directVolume: 0.75, directMuted: false, ambientVolume: 0.75, ambientMuted: false,
};

export function SoundWorkspacePanel() {
  const { inspectReferencedAsset, openImportBatch, project } = useEditor();
  const [workspace, setWorkspace] = useState<SoundWorkspace>();
  const [loadingError, setLoadingError] = useState('');
  const [entryError, setEntryError] = useState<{ nativeIndex: number; message: string }>();
  const [search, setSearch] = useState('');
  const [filter, setFilter] = useState<'all' | 'playable' | 'issues'>('all');
  const [preferences, setPreferences] = useState(DEFAULT_PREFERENCES);
  const [alwaysTrigger, setAlwaysTrigger] = useState<ReadonlySet<number>>(new Set());
  const [playback, setPlayback] = useState(EMPTY_PLAYBACK);
  const [scrollElement, setScrollElement] = useState<HTMLDivElement | null>(null);
  const runtime = useRef<SoundPreviewRuntime | undefined>(undefined);
  const playGeneration = useRef(0);
  const viewport = useVirtualGridViewport(scrollElement);

  useEffect(() => {
    let disposed = false;
    setWorkspace(undefined);
    setLoadingError('');
    setEntryError(undefined);
    setPlayback(EMPTY_PLAYBACK);
    setAlwaysTrigger(new Set());
    const nextRuntime = new SoundPreviewRuntime(
      (nativeIndex, preferAudible) => window.forge.getSoundPreview(nativeIndex, preferAudible),
      () => window.forge.cancelSoundPreview(),
      (state) => { if (!disposed) setPlayback(state); },
    );
    runtime.current = nextRuntime;
    void Promise.all([window.forge.getSoundWorkspace(), window.forge.getSettings()])
      .then(([soundWorkspace, settings]) => {
        if (disposed) return;
        const nextPreferences = readSoundPreviewPreferences(settings.entries);
        setWorkspace(soundWorkspace);
        setAlwaysTrigger(new Set(soundWorkspace.programs
          .filter((entry) => entry.hasRandomTriggerGate)
          .map((entry) => entry.nativeIndex)));
        setPreferences(nextPreferences);
        nextRuntime.setDirectVolume(nextPreferences.directVolume, nextPreferences.directMuted);
      })
      .catch((cause: unknown) => { if (!disposed) setLoadingError(errorMessage(cause)); });
    return () => {
      disposed = true;
      ++playGeneration.current;
      if (runtime.current === nextRuntime) runtime.current = undefined;
      void nextRuntime.dispose();
    };
  }, [project.projectId]);

  useEffect(() => {
    if (!playback.playing) return;
    const timer = window.setInterval(() => {
      const active = runtime.current;
      if (active) setPlayback((value) => ({ ...value, position: active.position }));
    }, 100);
    return () => window.clearInterval(timer);
  }, [playback.playing]);

  const items = useMemo(() => filterPrograms(workspace?.programs ?? [], search, filter), [filter, search, workspace]);
  const unsupportedCount = (workspace?.programs ?? []).filter((entry) => !entry.previewSupported).length;
  const windowed = virtualGridWindow(
    items.length, viewport.width, viewport.height, viewport.scrollTop, CARD_MIN_WIDTH, GRID_ROW_HEIGHT, 3,
  );

  const setPreference = <K extends keyof SoundPreviewPreferences>(key: K, value: SoundPreviewPreferences[K]) => {
    const next = { ...preferences, [key]: value };
    setPreferences(next);
    runtime.current?.setDirectVolume(next.directVolume, next.directMuted);
  };
  const persistPreference = (key: keyof SoundPreviewPreferences, value: number | boolean) => {
    const setting = ({
      directVolume: 'preview.sound.directVolume', directMuted: 'preview.sound.directMuted',
      ambientVolume: 'preview.sound.ambientVolume', ambientMuted: 'preview.sound.ambientMuted',
    } as const)[key];
    void window.forge.setSetting(setting, value).catch((cause: unknown) => setLoadingError(errorMessage(cause)));
  };
  const play = async (entry: SoundProgramEntry) => {
    const generation = ++playGeneration.current;
    setEntryError(undefined);
    try {
      await runtime.current?.play(
        entry.nativeIndex,
        !entry.hasRandomTriggerGate || alwaysTrigger.has(entry.nativeIndex),
      );
    }
    catch (cause) {
      if (generation === playGeneration.current)
        setEntryError({ nativeIndex: entry.nativeIndex, message: errorMessage(cause) });
    }
  };
  const stopPlayback = () => {
    ++playGeneration.current;
    runtime.current?.stop();
  };

  if (loadingError && !workspace) return <section aria-label="Sounds" className="editor-panel">
    <Alert color="red" title="Sound workspace could not be loaded">{loadingError}</Alert>
  </section>;
  if (!workspace) return <section aria-label="Sounds" className="editor-panel sound-workspace-loading">
    <Loader size="sm" /><Text size="sm">Reading sound and remap inventory…</Text>
  </section>;

  return <section aria-label="Sounds" className="editor-panel sound-workspace-panel">
    <div className="sound-workspace-controls">
      <Group justify="space-between" wrap="wrap">
        <Group gap="xs">
          <Text fw={600}>Sounds</Text>
          <Badge variant="light">{workspace.programs.length} programs</Badge>
          <Badge variant="outline">{workspace.sampleCount} samples</Badge>
          <Badge variant="outline">{workspace.mobyRemapCount} moby maps</Badge>
          <Badge variant="outline">{workspace.globalRemapCount} global remaps</Badge>
          {unsupportedCount > 0 && <Badge color="yellow" variant="light">
            {unsupportedCount} preview unsupported
          </Badge>}
        </Group>
        <Group gap="xs">
          {playback.nativeIndex !== undefined && <Text c="dimmed" size="xs">
            {playback.loading ? 'Loading' : playback.playing ? 'Playing' : 'Selected'} #{playback.nativeIndex}
          </Text>}
          <Button disabled={!playback.playing && !playback.loading} size="compact-xs" variant="default"
            onClick={stopPlayback}>Stop preview</Button>
          <Text c="dimmed" size="xs">Decoded cache: {formatBytes(runtime.current?.cacheBytes ?? 0)} / 32 MiB</Text>
        </Group>
      </Group>
      <div className="sound-workspace-filter-row">
        <TextInput aria-label="Search sounds" placeholder="Search native index, Asset ID, source, or moby class…"
          value={search} onChange={(event) => setSearch(event.currentTarget.value)} />
        <SegmentedControl aria-label="Filter sounds" data={[
          { label: 'All', value: 'all' }, { label: 'Playable', value: 'playable' },
          { label: 'Preview issues', value: 'issues' },
        ]} value={filter} onChange={(value) => setFilter(value as typeof filter)} />
      </div>
      {unsupportedCount > 0 && <Text c="dimmed" size="xs">
        Preview unsupported means Forge cannot safely emulate that sound program yet. The project data remains valid and is preserved.
      </Text>}
      <div className="sound-volume-grid">
        <VolumeControl label="Direct preview" muted={preferences.directMuted} volume={preferences.directVolume}
          onMuted={(value) => { setPreference('directMuted', value); persistPreference('directMuted', value); }}
          onVolume={(value) => setPreference('directVolume', value)}
          onVolumeCommit={(value) => persistPreference('directVolume', value)} />
        <VolumeControl label="Ambient preview" muted={preferences.ambientMuted} volume={preferences.ambientVolume}
          description="Used by ambient preview/simulation when available."
          onMuted={(value) => { setPreference('ambientMuted', value); persistPreference('ambientMuted', value); }}
          onVolume={(value) => setPreference('ambientVolume', value)}
          onVolumeCommit={(value) => persistPreference('ambientVolume', value)} />
      </div>
      {loadingError && <Alert color="red" withCloseButton onClose={() => setLoadingError('')}>{loadingError}</Alert>}
    </div>
    <div className="sound-workspace-scroll" ref={setScrollElement}>
      <div aria-label="Sound entries" className="sound-workspace-list" role="list"
        style={{ height: windowed.totalHeight }}>
        {items.slice(windowed.startIndex, windowed.endIndex).map((entry, offset) => {
          const index = windowed.startIndex + offset;
          const row = Math.floor(index / windowed.columns);
          const column = index % windowed.columns;
          const active = playback.nativeIndex === entry.nativeIndex;
          return <div className="sound-workspace-row-position" key={entry.nativeIndex} role="listitem"
            style={{
              transform: `translate(${column * (windowed.cardWidth + 8)}px, ${row * GRID_ROW_HEIGHT}px)`,
              width: windowed.cardWidth,
            }}>
            <SoundRow entry={entry} active={active} playback={playback}
              alwaysTrigger={alwaysTrigger.has(entry.nativeIndex)}
              error={entryError?.nativeIndex === entry.nativeIndex ? entryError.message : undefined}
              onPlay={() => void play(entry)}
              onStop={stopPlayback}
              onSeek={(value) => runtime.current?.seek(value)}
              onAlwaysTrigger={(checked) => setAlwaysTrigger((current) => {
                const next = new Set(current);
                if (checked) next.add(entry.nativeIndex);
                else next.delete(entry.nativeIndex);
                return next;
              })}
              onInspectMoby={(assetId) => void inspectReferencedAsset(assetId, 'Moby')}
              onOpenBatch={openImportBatch} />
          </div>;
        })}
      </div>
      {!items.length && <Text c="dimmed" className="editor-empty">No sound entries match these filters.</Text>}
    </div>
  </section>;
}

function SoundRow({
  entry, active, playback, alwaysTrigger, error,
  onPlay, onStop, onSeek, onAlwaysTrigger, onInspectMoby, onOpenBatch,
}: {
  entry: SoundProgramEntry;
  active: boolean;
  playback: SoundPreviewPlaybackState;
  alwaysTrigger: boolean;
  error?: string;
  onPlay(): void;
  onStop(): void;
  onSeek(value: number): void;
  onAlwaysTrigger(value: boolean): void;
  onInspectMoby(assetId: string): void;
  onOpenBatch(batchId: string): void;
}) {
  const sample = entry.samples[0];
  const remaps = [
    entry.globalRemapSlots.length ? `global ${entry.globalRemapSlots.join(', ')}` : '',
    ...entry.mobyDependents.map((value) => `moby 0x${value.classId.toString(16).toUpperCase()} [${value.remapSlots.join(', ')}]`),
  ].filter(Boolean).join(' · ') || 'No remap dependents';
  const sampleSummary = sample ? [
    `${entry.samples.length} sample${entry.samples.length === 1 ? '' : 's'}`,
    `${sample.sampleRate?.toLocaleString() ?? 'unknown'} Hz`,
    `${sample.channels} channel${sample.channels === 1 ? '' : 's'}`,
    `${sample.decodedFrameCount.toLocaleString()} frames`,
    sample.isLooping ? `loop ${sample.loopStartFrame ?? 0}–${sample.loopEndFrame}` : 'one-shot',
    formatBytes(sample.encodedLength),
  ].join(' · ') : 'No PCM sample dependency';
  const canStop = active && (playback.playing || playback.loading);
  const previewAction = canStop ? playback.loading ? 'Cancel' : 'Stop' : 'Play';
  const diagnostic = error ?? entry.diagnostic;
  return <div className="sound-workspace-row" data-supported={entry.previewSupported}>
    <Group className="sound-row-header" justify="space-between" wrap="nowrap">
      <Group className="sound-row-identity" gap={6} wrap="nowrap">
        <Text className="sound-row-name" fw={600} size="sm">Sound #{entry.nativeIndex}</Text>
        <Text className="sound-row-id" ff="monospace" size="xs" title={entry.assetId}>
          {entry.assetId?.slice(0, 12) ?? 'No Asset ID'}
        </Text>
        {!entry.previewSupported && <Badge color="yellow" size="xs" title={entry.diagnostic} variant="light">
          Unsupported
        </Badge>}
        {entry.hasRandomTriggerGate && <Checkbox
          aria-label={`Bypass random trigger gate for sound ${entry.nativeIndex}`}
          checked={alwaysTrigger}
          onChange={(event) => onAlwaysTrigger(event.currentTarget.checked)}
          size="xs"
          title="Bypass random trigger gate"
        />}
        {entry.mobyDependents.length > 0 && <Menu position="bottom-start" withinPortal>
          <Menu.Target>
            <Button size="compact-xs" variant="subtle">Mobys {entry.mobyDependents.length}</Button>
          </Menu.Target>
          <Menu.Dropdown>
            <Menu.Label>Moby dependents</Menu.Label>
            {entry.mobyDependents.map((dependent) => <Menu.Item
              disabled={!dependent.mobyAssetId}
              key={`${dependent.classId}:${dependent.mobyAssetId ?? 'missing'}`}
              onClick={() => { if (dependent.mobyAssetId) onInspectMoby(dependent.mobyAssetId); }}
            >
              0x{dependent.classId.toString(16).toUpperCase()} · slots {dependent.remapSlots.join(', ')}
              {!dependent.mobyAssetId && ' · missing asset'}
            </Menu.Item>)}
          </Menu.Dropdown>
        </Menu>}
      </Group>
      <Button
        aria-label={`${previewAction} sound ${entry.nativeIndex}`}
        className="sound-row-preview-button"
        disabled={!canStop && (!entry.previewSupported || playback.loading)}
        size="compact-xs"
        variant={canStop ? 'default' : 'filled'}
        onClick={canStop ? onStop : onPlay}
      >{previewAction}</Button>
    </Group>
    <div className="sound-row-metadata">
      <Group gap={4} wrap="nowrap">
        <Text className="sound-row-line" size="xs">
          Source: {entry.origin} · {entry.sourceGame} level {entry.sourceLevel} · batch
        </Text>
        {entry.importBatchId
          ? <Button aria-label={`Open import batch ${entry.importBatchId}`} p={0} size="compact-xs"
            title={entry.importBatchId} variant="transparent" onClick={() => onOpenBatch(entry.importBatchId!)}>
            {entry.importBatchId.slice(0, 14)}…
          </Button>
          : <Text size="xs">base</Text>}
      </Group>
      {diagnostic
        ? <Text className="sound-row-line" c={error ? 'red' : 'yellow'} role={error ? 'alert' : undefined}
          size="xs" title={diagnostic}>{diagnostic}</Text>
        : <Text className="sound-row-line" size="xs" title={`Group ${entry.volumeGroup} · Remaps: ${remaps}`}>
          Group {entry.volumeGroup} · Remaps: {remaps}
        </Text>}
      {active && playback.duration > 0
        ? <Group className="sound-row-seek" gap={6} wrap="nowrap">
          <Text size="xs" w={32}>{formatTime(playback.position)}</Text>
          <Slider aria-label={`Seek sound ${entry.nativeIndex}`} min={0} max={playback.duration} step={0.01}
            value={Math.min(playback.position, playback.duration)} onChange={onSeek} style={{ flex: 1 }} />
          <Text size="xs" w={32}>{formatTime(playback.duration)}</Text>
        </Group>
        : <Text className="sound-row-line" size="xs" title={sampleSummary}>{sampleSummary}</Text>}
    </div>
  </div>;
}

function VolumeControl({ label, description, muted, volume, onMuted, onVolume, onVolumeCommit }: {
  label: string;
  description?: string;
  muted: boolean;
  volume: number;
  onMuted(value: boolean): void;
  onVolume(value: number): void;
  onVolumeCommit(value: number): void;
}) {
  return <div className="sound-volume-control">
    <Group justify="space-between" wrap="nowrap">
      <Text size="xs" fw={500}>{label}: {Math.round(volume * 100)}%</Text>
      <Switch aria-label={`Mute ${label}`} checked={muted} size="xs"
        onChange={(event) => onMuted(event.currentTarget.checked)} label="Mute" />
    </Group>
    <Slider aria-label={`${label} volume`} disabled={muted} min={0} max={1} step={0.01}
      value={volume} onChange={onVolume} onChangeEnd={onVolumeCommit} />
    {description && <Text c="dimmed" size="xs">{description}</Text>}
  </div>;
}

function filterPrograms(
  programs: SoundProgramEntry[],
  search: string,
  filter: 'all' | 'playable' | 'issues',
): SoundProgramEntry[] {
  const needle = search.trim().toLocaleLowerCase();
  return programs.filter((entry) => {
    if (filter === 'playable' && !entry.previewSupported) return false;
    if (filter === 'issues' && entry.previewSupported) return false;
    if (!needle) return true;
    const text = [
      entry.nativeIndex, entry.assetId, entry.sourceContentId, entry.origin, entry.sourceGame, entry.sourceLevel,
      entry.importBatchId, ...entry.globalRemapSlots,
      ...entry.mobyDependents.flatMap((value) => [value.classId, `0x${value.classId.toString(16)}`, value.mobyAssetId]),
    ].filter((value) => value !== undefined).join(' ').toLocaleLowerCase();
    return text.includes(needle);
  });
}

function formatTime(value: number): string {
  const minutes = Math.floor(value / 60);
  return `${minutes}:${(value - minutes * 60).toFixed(1).padStart(4, '0')}`;
}

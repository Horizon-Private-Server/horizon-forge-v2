import { Badge, Button, Group, Stack, Text } from '@mantine/core';
import { useEffect, useRef, useState } from 'react';

import type { AssetExplorerSource } from '../../types/AssetExplorer.js';
import type { SoundProgramEntry, SoundWorkspace } from '../../types/SoundWorkspace.js';
import { errorMessage } from '../../utils/Errors.ts';
import { readSoundPreviewPreferences } from '../../utils/SoundPreviewPreferences.ts';
import { forgeLog } from '../log/LogStore.ts';
import { useLoggedMessage } from '../log/UseLog.ts';
import { soundSlotsForMoby } from '../../utils/SoundWorkspace.ts';
import {
  SoundPreviewRuntime, type SoundPreviewPlaybackState,
} from './SoundPreviewRuntime.ts';

const EMPTY_PLAYBACK: SoundPreviewPlaybackState = {
  playing: false, loading: false, position: 0, duration: 0,
};

interface MobySoundButtonsProps {
  assetId: string;
  classId: number;
  projectId: string;
  sources: AssetExplorerSource[];
}

export function MobySoundButtons({ assetId, classId, projectId, sources }: MobySoundButtonsProps) {
  const [workspace, setWorkspace] = useState<SoundWorkspace>();
  const [loadingError, setLoadingError] = useLoggedMessage('Moby sounds');
  const [entryError, setEntryError] = useState<{ nativeIndex: number; message: string }>();
  const [playback, setPlayback] = useState(EMPTY_PLAYBACK);
  const runtime = useRef<SoundPreviewRuntime | undefined>(undefined);
  const catalogPreviewSource = useRef<{ bankAssetId: string; game: string } | undefined>(undefined);
  const [activeCatalogSource, setActiveCatalogSource] = useState('');
  const playGeneration = useRef(0);
  const catalogSources = sources.filter((source) => source.mobySounds !== undefined);

  useEffect(() => {
    let disposed = false;
    setWorkspace(undefined);
    setLoadingError('');
    setEntryError(undefined);
    setPlayback(EMPTY_PLAYBACK);
    const nextRuntime = new SoundPreviewRuntime(
      (nativeIndex, preferAudible) => window.forge.getSoundPreview(
        nativeIndex, preferAudible, catalogPreviewSource.current,
      ),
      () => window.forge.cancelSoundPreview(),
      (state) => { if (!disposed) setPlayback(state); },
    );
    runtime.current = nextRuntime;
    void Promise.all([
      catalogSources.length === 0 ? window.forge.getSoundWorkspace() : Promise.resolve(undefined),
      window.forge.getSettings(),
    ])
      .then(([soundWorkspace, settings]) => {
        if (disposed) return;
        const preferences = readSoundPreviewPreferences(settings.entries);
        nextRuntime.setDirectVolume(preferences.directVolume, preferences.directMuted);
        setWorkspace(soundWorkspace);
      })
      .catch((cause: unknown) => { if (!disposed) setLoadingError(errorMessage(cause)); });
    return () => {
      disposed = true;
      ++playGeneration.current;
      if (runtime.current === nextRuntime) runtime.current = undefined;
      void nextRuntime.dispose();
    };
  }, [projectId, catalogSources.length]);

  useEffect(() => {
    ++playGeneration.current;
    runtime.current?.stop();
    catalogPreviewSource.current = undefined;
    setActiveCatalogSource('');
    setEntryError(undefined);
  }, [assetId, classId]);

  const slots = soundSlotsForMoby(workspace?.programs ?? [], classId);
  const play = async (
    entry: Pick<SoundProgramEntry, 'nativeIndex'>,
    catalogSource?: { bankAssetId: string; game: string },
    sourceKey = '',
  ) => {
    const generation = ++playGeneration.current;
    setEntryError(undefined);
    catalogPreviewSource.current = catalogSource;
    setActiveCatalogSource(sourceKey);
    try {
      await runtime.current?.play(
        entry.nativeIndex,
        true,
        sourceKey ? `${sourceKey}:${entry.nativeIndex}` : String(entry.nativeIndex),
      );
    } catch (cause) {
      if (generation === playGeneration.current) {
        setEntryError({ nativeIndex: entry.nativeIndex, message: errorMessage(cause) });
        forgeLog.write('Moby sounds', 'error', `Sound ${entry.nativeIndex}: ${errorMessage(cause)}`);
      }
    }
  };
  const stop = () => {
    ++playGeneration.current;
    runtime.current?.stop();
    setActiveCatalogSource('');
  };

  return <Stack gap="xs">
    <Group gap="xs">
      <Text fw={600} size="sm">Moby sounds</Text>
      {catalogSources.length > 0 && <Badge variant="light">
        {catalogSources.reduce((count, source) => count + (source.mobySounds?.slots.length ?? 0), 0)}
      </Badge>}
      {catalogSources.length === 0 && workspace && <Badge variant="light">{slots.length}</Badge>}
    </Group>
    {catalogSources.length === 0 && !workspace && !loadingError
      && <Text c="dimmed" size="sm">Reading sound remaps…</Text>}
    {loadingError && <Text c="dimmed" size="sm">Sound remaps unavailable. See Log.</Text>}
    {catalogSources.length === 0 && workspace && slots.length === 0 && <Text c="dimmed" size="sm">
      This moby has no sound remaps in the current level.
    </Text>}
    {catalogSources.map((source) => {
      const sounds = source.mobySounds!;
      const sourceKey = `${source.game}:${source.revision}:${source.level}:${sounds.bankAssetId}`;
      return <Stack gap={4} key={sourceKey}>
        <Text c="dimmed" size="xs">{source.game} · {source.level}</Text>
        {sounds.slots.length === 0
          ? <Text c="dimmed" size="sm">No sound remaps.</Text>
          : <Group gap="xs" wrap="wrap">{sounds.slots.map((slot) => {
            const active = activeCatalogSource === sourceKey && playback.nativeIndex === slot.nativeProgramIndex;
            const stopping = active && playback.playing;
            return <Button
              aria-label={`${stopping ? 'Stop' : 'Play'} moby sound slot ${slot.slot}, sound ${slot.nativeProgramIndex}`}
              aria-pressed={stopping}
              disabled={!slot.previewSupported}
              key={`${slot.slot}:${slot.nativeProgramIndex}`}
              loading={active && playback.loading}
              size="compact-xs"
              title={slot.previewSupported
                ? `${stopping ? 'Stop' : 'Play'} ${source.level} moby sound slot ${slot.slot}`
                : slot.previewBlocker}
              variant={active ? 'filled' : 'light'}
              onClick={() => stopping ? stop() : void play(
                { nativeIndex: slot.nativeProgramIndex },
                { bankAssetId: sounds.bankAssetId, game: source.game },
                sourceKey,
              )}
            >Slot {slot.slot} · #{slot.nativeProgramIndex}</Button>;
          })}</Group>}
      </Stack>;
    })}
    {catalogSources.length === 0 && slots.length > 0 && <Group gap="xs" wrap="wrap">
      {slots.map(({ slot, program }) => {
        const active = playback.nativeIndex === program.nativeIndex;
        const stopping = active && playback.playing;
        return <Button
          aria-label={`${stopping ? 'Stop' : 'Play'} moby sound slot ${slot}, sound ${program.nativeIndex}`}
          aria-pressed={stopping}
          disabled={!program.previewSupported}
          key={`${slot}:${program.nativeIndex}`}
          loading={active && playback.loading}
          size="compact-xs"
          title={program.previewSupported ? `${stopping ? 'Stop' : 'Play'} moby sound slot ${slot}` : program.diagnostic}
          variant={active ? 'filled' : 'light'}
          onClick={() => stopping ? stop() : void play(program)}
        >Slot {slot} · #{program.nativeIndex}</Button>;
      })}
    </Group>}
    {entryError && <Text c="red" role="alert" size="sm">
      Sound #{entryError.nativeIndex}: {entryError.message}
    </Text>}
  </Stack>;
}

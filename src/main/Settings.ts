import { readFile } from 'node:fs/promises';
import path from 'node:path';

import { writeJsonSafely } from '../utils/FileSystem.ts';
import type { KnownSettings, SettingEntry, SettingsSnapshot, SettingValue } from '../types/ForgeApi.js';
import type { ApplicationPaths, RawSettings, SettingDefinition } from '../types/Settings.js';

export class SettingsStore {
  readonly paths: ApplicationPaths;
  private readonly definitions: SettingDefinition[];
  private writes: Promise<void> = Promise.resolve();

  constructor(paths: ApplicationPaths) {
    this.paths = paths;
    this.definitions = createDefinitions(paths);
  }

  async ensureFile(): Promise<void> {
    try {
      await readFile(this.paths.settingsFile);
    } catch (error) {
      if (!isMissingFile(error)) throw error;
      await this.enqueueWrite(() => writeJsonSafely(this.paths.settingsFile, {}));
    }
  }

  async getSnapshot(): Promise<SettingsSnapshot> {
    const raw = await this.readRaw();
    const entries = this.definitions.map((definition) => {
      const stored = raw.values[definition.key];
      const valid = stored === undefined || definition.validate(stored);
      const diagnostic = valid ? undefined : `Invalid value for ${definition.key}; using its default.`;
      return {
        key: definition.key,
        group: definition.group,
        label: definition.label,
        description: definition.description,
        type: definition.type,
        value: stored === undefined || !valid ? definition.defaultValue : stored,
        defaultValue: definition.defaultValue,
        restartRequired: definition.restartRequired,
        editable: definition.editable,
        diagnostic,
      } satisfies SettingEntry;
    });

    return {
      entries,
      diagnostics: [...raw.diagnostics, ...entries.flatMap((entry) => entry.diagnostic ? [entry.diagnostic] : [])],
    };
  }

  set(key: string, value: unknown): Promise<SettingsSnapshot> {
    return this.enqueueWrite(async () => {
      const definition = this.definitions.find((candidate) => candidate.key === key);
      if (!definition) throw new TypeError(`Unknown setting: ${key}`);
      if (!definition.editable) throw new TypeError(`${key} is managed by Forge setup`);
      if (!definition.validate(value)) throw new TypeError(`Invalid value for ${key}`);

      const raw = await this.readRaw();
      if (raw.malformed) throw new Error('Settings file is malformed; reset all settings before making changes.');
      raw.values[key] = value;
      await writeJsonSafely(this.paths.settingsFile, raw.values);
      return this.getSnapshot();
    });
  }

  setMany(values: Partial<KnownSettings>): Promise<SettingsSnapshot> {
    return this.enqueueWrite(async () => {
      const raw = await this.readRaw();
      if (raw.malformed) throw new Error('Settings file is malformed; reset all settings before making changes.');
      for (const [key, value] of Object.entries(values)) {
        const definition = this.definitions.find((candidate) => candidate.key === key);
        if (!definition || !definition.validate(value)) throw new TypeError(`Invalid value for ${key}`);
        raw.values[key] = value;
      }
      await writeJsonSafely(this.paths.settingsFile, raw.values);
      return this.getSnapshot();
    });
  }

  reset(key?: string): Promise<SettingsSnapshot> {
    return this.enqueueWrite(async () => {
      if (key && !this.definitions.some((definition) => definition.key === key)) {
        throw new TypeError(`Unknown setting: ${key}`);
      }

      const raw = await this.readRaw();
      if (raw.malformed && key) throw new Error('Settings file is malformed; use reset all instead.');
      if (raw.malformed) raw.values = {};
      else if (key) delete raw.values[key];
      else for (const definition of this.definitions) delete raw.values[definition.key];
      await writeJsonSafely(this.paths.settingsFile, raw.values);
      return this.getSnapshot();
    });
  }

  async export(includeMachinePaths: boolean): Promise<Record<string, SettingValue>> {
    const snapshot = await this.getSnapshot();
    const machineKeys = new Set(this.definitions.filter((definition) => definition.machineSpecific).map((definition) => definition.key));
    return Object.fromEntries(snapshot.entries
      .filter((entry) => includeMachinePaths || !machineKeys.has(entry.key))
      .map((entry) => [entry.key, entry.value])) as Record<string, SettingValue>;
  }

  private async readRaw(): Promise<RawSettings> {
    try {
      const parsed: unknown = JSON.parse(await readFile(this.paths.settingsFile, 'utf8'));
      if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
        return { values: {}, diagnostics: ['Settings file must contain a JSON object; using defaults.'], malformed: true };
      }
      return { values: parsed as Record<string, unknown>, diagnostics: [], malformed: false };
    } catch (error) {
      if (isMissingFile(error)) return { values: {}, diagnostics: [], malformed: false };
      if (error instanceof SyntaxError) {
        return { values: {}, diagnostics: ['Settings file contains invalid JSON; using defaults.'], malformed: true };
      }
      throw error;
    }
  }

  private enqueueWrite<T>(operation: () => Promise<T>): Promise<T> {
    const result = this.writes.then(operation, operation);
    this.writes = result.then(() => undefined, () => undefined);
    return result;
  }
}

function createDefinitions(paths: ApplicationPaths): SettingDefinition[] {
  const absolutePath = (value: unknown): value is string => typeof value === 'string'
    && (value === '' || path.posix.isAbsolute(value) || path.win32.isAbsolute(value));
  return [
    {
      key: 'editor.autosaveSeconds', group: 'Editor', label: 'Autosave interval', type: 'number',
      description: 'Seconds between project autosaves.', defaultValue: 30, restartRequired: false, machineSpecific: false,
      editable: true,
      validate: (value): value is number => Number.isInteger(value) && Number(value) >= 5 && Number(value) <= 3600,
    },
    {
      key: 'imports.uya.enabled', group: 'Imports', label: 'Import UYA assets', type: 'boolean',
      description: 'Import reusable UYA assets after setup.', defaultValue: true, restartRequired: false, machineSpecific: false,
      editable: true,
      validate: (value): value is boolean => typeof value === 'boolean',
    },
    {
      key: 'paths.projects', group: 'Paths', label: 'Projects directory', type: 'path',
      description: 'Default location for Forge projects.', defaultValue: paths.defaultProjects, restartRequired: false, machineSpecific: true,
      editable: true,
      validate: (value): value is string => absolutePath(value) && value.length > 0,
    },
    {
      key: 'paths.developmentIsos', group: 'Paths', label: 'Development ISO directory', type: 'path',
      description: 'Default location for writable development ISOs.', defaultValue: paths.defaultDevelopmentIsos, restartRequired: false, machineSpecific: true,
      editable: true,
      validate: (value): value is string => absolutePath(value) && value.length > 0,
    },
    {
      key: 'sources.uya.iso', group: 'Sources', label: 'Clean UYA ISO', type: 'path',
      description: 'Verified clean NTSC-U source ISO; change it through Setup.', defaultValue: '', restartRequired: false, machineSpecific: true,
      editable: false,
      validate: absolutePath,
    },
    {
      key: 'sources.uya.game', group: 'Sources', label: 'Source game', type: 'text',
      description: 'Detected source game.', defaultValue: '', restartRequired: false, machineSpecific: true, editable: false,
      validate: (value): value is string => typeof value === 'string' && (value === '' || value === 'UYA'),
    },
    {
      key: 'sources.uya.region', group: 'Sources', label: 'Source region', type: 'text',
      description: 'Detected source region.', defaultValue: '', restartRequired: false, machineSpecific: true, editable: false,
      validate: (value): value is string => typeof value === 'string',
    },
    {
      key: 'sources.uya.revision', group: 'Sources', label: 'Source revision', type: 'text',
      description: 'Detected source revision.', defaultValue: '', restartRequired: false, machineSpecific: true, editable: false,
      validate: (value): value is string => typeof value === 'string',
    },
    {
      key: 'sources.uya.serial', group: 'Sources', label: 'Source serial', type: 'text',
      description: 'Detected source serial.', defaultValue: '', restartRequired: false, machineSpecific: true, editable: false,
      validate: (value): value is string => typeof value === 'string',
    },
    {
      key: 'sources.uya.size', group: 'Sources', label: 'Source size', type: 'number',
      description: 'Verified source size in bytes.', defaultValue: 0, restartRequired: false, machineSpecific: true, editable: false,
      validate: (value): value is number => Number.isSafeInteger(value) && Number(value) >= 0,
    },
    {
      key: 'sources.uya.fingerprint', group: 'Sources', label: 'Source MD5', type: 'text',
      description: 'PCSX2/Redump-compatible data-track checksum.', defaultValue: '', restartRequired: false, machineSpecific: true, editable: false,
      validate: (value): value is string => typeof value === 'string' && (value === '' || /^[0-9a-f]{32}$/i.test(value)),
    },
    {
      key: 'targets.uya.developmentIso', group: 'Targets', label: 'Development UYA ISO', type: 'path',
      description: 'Writable ISO patched by Forge.', defaultValue: '', restartRequired: false, machineSpecific: true,
      editable: false,
      validate: absolutePath,
    },
  ];
}

function isMissingFile(error: unknown): boolean {
  return typeof error === 'object' && error !== null && 'code' in error && error.code === 'ENOENT';
}

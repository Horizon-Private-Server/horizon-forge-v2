import type { KnownSettings, SettingEntry, SettingKey } from './ForgeApi.js';

export interface ApplicationPaths {
  data: string;
  settingsFile: string;
  assets: string;
  renderCache: string;
  logs: string;
  defaultProjects: string;
  defaultDevelopmentIsos: string;
}

export interface SettingDefinition<K extends SettingKey = SettingKey> {
  key: K;
  group: string;
  label: string;
  description: string;
  type: SettingEntry['type'];
  defaultValue: KnownSettings[K];
  restartRequired: boolean;
  machineSpecific: boolean;
  editable: boolean;
  validate(value: unknown): value is KnownSettings[K];
}

export interface RawSettings {
  values: Record<string, unknown>;
  diagnostics: string[];
  malformed: boolean;
}

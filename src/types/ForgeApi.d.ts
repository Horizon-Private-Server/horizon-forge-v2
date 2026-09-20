export interface ForgeHostStatus {
  hostVersion: string;
  sdkRevision: string;
  supportedGames: string[];
  capabilities: string[];
}

export interface KnownSettings {
  'editor.autosaveSeconds': number;
  'imports.uya.enabled': boolean;
  'paths.projects': string;
  'paths.developmentIsos': string;
  'sources.uya.fingerprint': string;
  'sources.uya.game': string;
  'sources.uya.iso': string;
  'sources.uya.region': string;
  'sources.uya.revision': string;
  'sources.uya.serial': string;
  'sources.uya.size': number;
  'targets.uya.developmentIso': string;
}

export type SettingKey = keyof KnownSettings;
export type SettingValue = KnownSettings[SettingKey];

export interface SettingEntry {
  key: SettingKey;
  group: string;
  label: string;
  description: string;
  type: 'boolean' | 'number' | 'path' | 'text';
  value: SettingValue;
  defaultValue: SettingValue;
  restartRequired: boolean;
  editable: boolean;
  diagnostic?: string;
}

export interface SettingsSnapshot {
  entries: SettingEntry[];
  diagnostics: string[];
}

export interface Progress {
  completed: number;
  total: number;
}

export interface UyaIsoIdentity {
  isSupported: boolean;
  game: string;
  region: string;
  revision: string;
  serial: string;
  size: number;
  fingerprint: string;
  diagnostic: string;
}

export interface DevelopmentIsoResult {
  path: string;
  size: number;
  fingerprint: string;
}

export interface SetupState {
  required: boolean;
  projectsDirectory: string;
  developmentIsoDirectory: string;
  sourceIso: string;
  developmentIso: string;
  importUyaAssets: boolean;
  source?: Omit<UyaIsoIdentity, 'isSupported' | 'diagnostic'>;
  availableBytes?: number;
}

export interface SetupProgress extends Progress {
  operation: 'validate' | 'copy';
}

export type ForgeDialog = 'setup' | 'settings';

export interface ForgeApi {
  getHostStatus(): Promise<ForgeHostStatus>;
  getTfragUrl(): Promise<string | undefined>;
  echo(message: string): Promise<string>;
  getSettings(): Promise<SettingsSnapshot>;
  setSetting(key: string, value: unknown): Promise<SettingsSnapshot>;
  resetSettings(key?: string): Promise<SettingsSnapshot>;
  exportSettings(includeMachinePaths: boolean): Promise<boolean>;
  getSetupState(): Promise<SetupState>;
  chooseSetupDirectory(kind: 'projects' | 'developmentIsos'): Promise<SetupState>;
  chooseUyaSource(): Promise<{ path: string; identity: UyaIsoIdentity } | undefined>;
  createDevelopmentIso(): Promise<DevelopmentIsoResult | undefined>;
  cancelSetupOperation(): Promise<void>;
  onSetupProgress(listener: (progress: SetupProgress) => void): () => void;
  onOpenDialog(listener: (dialog: ForgeDialog) => void): () => void;
}

export interface ForgeHostStatus {
  hostVersion: string;
  sdkRevision: string;
  supportedGames: string[];
  capabilities: string[];
}

export interface KnownSettings {
  'editor.autosaveSeconds': number;
  'imports.uya.enabled': boolean;
  'imports.uya.completedFingerprint': string;
  'imports.uya.completedVersion': number;
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

export interface UyaAssetImportResult {
  completedLevels: number;
  totalLevels: number;
  assetAppearances: number;
  uniqueAssets: number;
  failedAssets: number;
  resumed: boolean;
}

export interface UyaProjectOptions {
  levels: number[];
  warnings: string[];
}

export interface UyaProjectPreflight {
  level: number;
  sourceInstanceCount: number;
  renderableInstanceCount: number;
  modelLessInstanceCount: number;
  missingAssetInstanceCount: number;
  missingClassCount: number;
  warnings: string[];
}

export interface ForgeProjectDescriptor {
  path: string;
  name: string;
  targetGame: string;
  targetRegion: string;
  targetRevision: string;
  bakeProfile: string;
  baseLevel: number;
  modifiedUnixMilliseconds: number;
  entityCount: number;
  missingAssetCount: number;
  isDirty: boolean;
  migrationPending: boolean;
  warnings: string[];
  recoveries: ProjectRecoverySnapshot[];
}

export interface ProjectRecoverySnapshot {
  id: string;
  createdUnixMilliseconds: number;
  name: string;
  entityCount: number;
  fingerprint: string;
  size: number;
}

export interface RecentForgeProject {
  path: string;
  project?: ForgeProjectDescriptor;
  error?: string;
}

export interface ProjectHubState {
  projectsDirectory: string;
  creation?: UyaProjectOptions;
  recentProjects: RecentForgeProject[];
  diagnostic?: string;
}

export interface SetupState {
  required: boolean;
  projectsDirectory: string;
  developmentIsoDirectory: string;
  sourceIso: string;
  developmentIso: string;
  developmentIsoReady: boolean;
  importUyaAssets: boolean;
  assetImportComplete: boolean;
  source?: Omit<UyaIsoIdentity, 'isSupported' | 'diagnostic'>;
  availableBytes?: number;
}

export interface SetupProgress extends Progress {
  operation: 'validate' | 'copy' | 'import';
}

export type ForgeDialog = 'setup' | 'settings';

export type ForgeAction = ForgeDialog | 'projects' | 'newProject' | 'openProject';

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
  importUyaAssets(force?: boolean): Promise<UyaAssetImportResult>;
  getProjectHub(): Promise<ProjectHubState>;
  preflightUyaProject(level: number): Promise<UyaProjectPreflight>;
  createUyaProject(name: string, level: number, allowPartial: boolean): Promise<ForgeProjectDescriptor | undefined>;
  openForgeProject(): Promise<ForgeProjectDescriptor | undefined>;
  openRecentProject(path: string): Promise<ForgeProjectDescriptor>;
  renameForgeProject(path: string, name: string): Promise<ForgeProjectDescriptor>;
  restoreForgeProject(path: string, recoveryId: string): Promise<ForgeProjectDescriptor>;
  migrateForgeProject(path: string): Promise<ForgeProjectDescriptor>;
  removeRecentProject(path: string): Promise<ProjectHubState>;
  revealForgeProject(path: string): Promise<void>;
  cancelSetupOperation(): Promise<void>;
  onSetupProgress(listener: (progress: SetupProgress) => void): () => void;
  onForgeAction(listener: (action: ForgeAction) => void): () => void;
}

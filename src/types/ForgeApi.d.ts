import type { EditorCommand, EditorEvent, EditorSnapshot } from './EditorRuntime.js';
import type { ForgeNotification } from './Notifications.js';
import type { UpdateCheckResult } from './Updates.js';

export type { EditorCommand, EditorEvent, EditorSnapshot } from './EditorRuntime.js';
export type { ForgeNotification } from './Notifications.js';

export interface ForgeHostStatus {
  hostVersion: string;
  sdkRevision: string;
  supportedGames: string[];
  capabilities: string[];
}

export interface KnownSettings {
  'editor.autosaveSeconds': number;
  'keybindings.overrides': string;
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
  'ui.editorLayout': string;
  'ui.showViewportStats': boolean;
  'updates.automaticChecks': boolean;
  'updates.channel': 'stable' | 'nightly';
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

export interface BuildPatchProgress extends Progress {
  phase: string;
  message: string;
}

export interface BuildPatchResult {
  succeeded: boolean;
  requiresWarningAcknowledgement: boolean;
  warningCodes: string[];
  diagnostics: string[];
  message: string;
  nextAction: string;
  developmentIsoPath?: string;
  patchMode?: 'InPlace' | 'FullImageReplacement';
  outputLevelWadSha256?: string;
  bakedLayerCount: number;
  bakeWasCurrent: boolean;
}

export type BuildLayerId = 'World' | 'Sky' | 'Tfrags' | 'Collision' | 'Ties'
  | 'Shrubs' | 'Mobys' | 'Gameplay' | 'Lighting' | 'Opaque';

export interface BuildLayerStatus {
  layer: BuildLayerId;
  state: 'Dirty' | 'DependencyInvalidated' | 'Clean' | 'Blocked';
  canDefer: boolean;
}

export interface BuildPlan {
  layers: BuildLayerStatus[];
}

export interface EditorLoadProgress extends Progress {
  label: string;
  status: 'loading' | 'error';
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
  missingAssets: MissingProjectAsset[];
}

export interface MissingProjectAsset {
  id: string;
  kind: string;
  entityCount: number;
  repairable: boolean;
  provenance: string[];
}

export interface CatalogMaintenancePreview {
  projectCount: number;
  catalogAssetCount: number;
  protectedAssetCount: number;
  candidateCount: number;
  catalogCandidateCount: number;
  candidateBytes: number;
  confirmationToken: string;
  candidateKinds: string[];
  blockers: string[];
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

export interface EditorSceneEnvironment {
  backgroundColor: [number, number, number];
  fogColor: [number, number, number];
  fogNearDistance: number;
  fogFarDistance: number;
  fogNearIntensity: number;
  fogFarIntensity: number;
}

export interface EditorTerrainSource {
  urls: string[];
  skyUrl?: string;
  environment?: EditorSceneEnvironment;
  assets: { assetId: string; kind: 'moby' | 'tie' | 'shrub'; url?: string; error?: string }[];
  cacheHit: boolean;
}

export type ForgeDialog = 'setup' | 'settings';

export type EditorLayoutAction = 'resetLayout' | 'showViewport' | 'showSceneTree'
  | 'showProperties' | 'showDiagnostics' | 'showBuild';

export type ForgeAction = ForgeDialog | EditorLayoutAction
  | 'projects' | 'newProject' | 'openProject' | 'saveProject' | 'undoEditor' | 'redoEditor'
  | 'deleteEntities' | 'duplicateEntities' | 'copyEntities' | 'pasteEntities' | 'checkUpdates';

export type ForgeWindowAction = 'quit' | 'minimize' | 'toggleMaximize'
  | 'resetZoom' | 'zoomIn' | 'zoomOut' | 'toggleFullscreen' | 'toggleDevTools';

export interface ForgeApi {
  getHostStatus(): Promise<ForgeHostStatus>;
  echo(message: string): Promise<string>;
  getSettings(): Promise<SettingsSnapshot>;
  setSetting(key: string, value: unknown): Promise<SettingsSnapshot>;
  resetSettings(key?: string): Promise<SettingsSnapshot>;
  exportSettings(includeMachinePaths: boolean): Promise<boolean>;
  checkForUpdates(): Promise<UpdateCheckResult>;
  getNotifications(): Promise<ForgeNotification[]>;
  dismissNotification(id: string): Promise<void>;
  runNotificationAction(id: string): Promise<void>;
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
  repairForgeProjectAssets(path: string): Promise<ForgeProjectDescriptor>;
  previewCatalogGarbageCollection(): Promise<CatalogMaintenancePreview>;
  collectCatalogGarbage(confirmationToken: string): Promise<CatalogMaintenancePreview>;
  removeRecentProject(path: string): Promise<ProjectHubState>;
  revealForgeProject(path: string): Promise<void>;
  revealLogs(): Promise<void>;
  openEditorProject(path: string): Promise<EditorSnapshot>;
  closeEditorProject(): Promise<void>;
  getEditorSnapshot(): Promise<EditorSnapshot>;
  executeEditorCommand(command: EditorCommand): Promise<EditorSnapshot>;
  saveEditorProject(): Promise<EditorSnapshot>;
  getBuildPlan(): Promise<BuildPlan>;
  buildAndPatchProject(includedLayers: BuildLayerId[]): Promise<BuildPatchResult>;
  cancelBuildAndPatch(): Promise<void>;
  runWindowAction(action: ForgeWindowAction): void;
  setEditorTextInputActive(active: boolean): void;
  readEditorEvents(afterSequence: number, limit?: number): Promise<EditorEvent[]>;
  getEditorTerrain(): Promise<EditorTerrainSource>;
  cancelEditorTerrain(): Promise<void>;
  cancelSetupOperation(): Promise<void>;
  onEditorTerrainProgress(listener: (progress: Progress) => void): () => void;
  onBuildPatchProgress(listener: (progress: BuildPatchProgress) => void): () => void;
  onSetupProgress(listener: (progress: SetupProgress) => void): () => void;
  onForgeAction(listener: (action: ForgeAction) => void): () => void;
  onNotificationsChanged(listener: (notifications: ForgeNotification[]) => void): () => void;
}

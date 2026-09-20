import path from 'node:path';

import type { ApplicationPaths } from '../types/Settings.js';

export function createApplicationPaths(userData: string, documents: string, logs = path.join(userData, 'logs')): ApplicationPaths {
  const data = path.resolve(userData);
  return {
    data,
    settingsFile: path.join(data, 'settings.json'),
    assets: path.join(data, 'assets'),
    renderCache: path.join(data, 'render-cache'),
    logs: path.resolve(logs),
    defaultProjects: path.resolve(documents, 'Horizon Forge Projects'),
    defaultDevelopmentIsos: path.resolve(documents, 'Horizon Forge Development ISOs'),
  };
}

export function safeProjectDirectoryName(name: string): string {
  return name.trim().replace(/[<>:"/\\|?*\u0000-\u001f]/g, '-').replace(/[. ]+$/g, '') || 'New Project';
}

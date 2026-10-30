import { app } from 'electron';
import path from 'node:path';

export function hostLaunch(): { command: string; args: string[] } {
  if (app.isPackaged) return {
    command: path.join(process.resourcesPath, 'host', process.platform === 'win32' ? 'Forge.Host.exe' : 'Forge.Host'),
    args: [],
  };
  return { command: 'dotnet', args: [process.env.FORGE_HOST_PATH
    ?? path.join(app.getAppPath(), 'src/Forge.Host/bin/Release/net10.0/Forge.Host.dll')] };
}

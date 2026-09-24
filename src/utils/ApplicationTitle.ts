import type { UpdateChannel } from '../types/Updates.js';

export function applicationTitle(packaged: boolean, version: string, channel?: UpdateChannel): string {
  if (!packaged) return 'Horizon Forge - Local Dev';
  return channel ? `Horizon Forge v${version}` : 'Horizon Forge - Standalone';
}

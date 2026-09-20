import { net, protocol } from 'electron';
import { realpath } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import { isPathInside } from '../utils/Security.js';

export async function registerTfragProtocol(tfragPath: string | undefined): Promise<string | undefined> {
  if (!tfragPath) return undefined;

  try {
    const modelPath = await realpath(tfragPath);
    const root = await realpath(path.dirname(modelPath));

    protocol.handle('forge-asset', async (request) => {
      if (request.method !== 'GET') return new Response(null, { status: 405 });

      try {
        const url = new URL(request.url);
        if (url.hostname !== 'local') return new Response(null, { status: 404 });
        const candidate = await realpath(path.resolve(root, decodeURIComponent(url.pathname).replace(/^\/+/, '')));
        if (!isPathInside(root, candidate)) return new Response(null, { status: 404 });
        return net.fetch(pathToFileURL(candidate).href);
      } catch (error) {
        console.warn(`Could not serve ${request.url}`, error);
        return new Response(null, { status: 404 });
      }
    });

    return `forge-asset://local/${encodeURIComponent(path.basename(modelPath))}`;
  } catch (error) {
    console.warn(`Tfrag fixture is unavailable at ${tfragPath}`, error);
    return undefined;
  }
}

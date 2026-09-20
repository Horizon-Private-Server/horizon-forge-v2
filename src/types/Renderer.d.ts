/// <reference types="vite/client" />

import type { ForgeApi } from './ForgeApi.js';

declare global {
  interface Window {
    forge: ForgeApi;
  }
}

export {};

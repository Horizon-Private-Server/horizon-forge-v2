/// <reference types="vite/client" />

import type { ForgeApi } from './ForgeApi.js';
import type { OcclusionWorkerApi } from './OcclusionWorker.js';

declare global {
  // lib.dom defines WebGPU interfaces but omits these runtime flag namespaces.
  const GPUBufferUsage: Readonly<Record<'MAP_READ' | 'MAP_WRITE' | 'COPY_SRC' | 'COPY_DST' | 'INDEX'
    | 'VERTEX' | 'UNIFORM' | 'STORAGE' | 'INDIRECT' | 'QUERY_RESOLVE', number>>;
  const GPUTextureUsage: Readonly<Record<'COPY_SRC' | 'COPY_DST' | 'TEXTURE_BINDING' | 'STORAGE_BINDING' | 'RENDER_ATTACHMENT', number>>;
  const GPUShaderStage: Readonly<Record<'VERTEX' | 'FRAGMENT' | 'COMPUTE', number>>;
  const GPUMapMode: Readonly<Record<'READ' | 'WRITE', number>>;

  interface Window {
    forge: ForgeApi;
    occlusion: OcclusionWorkerApi;
  }
}

export {};

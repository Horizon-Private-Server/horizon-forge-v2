export type EditorSnapSource = 'center' | 'origin' | 'vertex';
export type EditorSnapTarget = 'grid' | 'center' | 'vertex' | 'surface';

export interface EditorTransformTarget {
  key: string;
  translateOnly?: boolean;
  transform: import('./EditorRuntime.js').ProjectTransform;
  vertices(): import('three').Vector3[];
  preview(transform?: import('./EditorRuntime.js').ProjectTransform, axis?: string | null): boolean;
  commit(transform: import('./EditorRuntime.js').ProjectTransform, axis?: string | null): void;
}

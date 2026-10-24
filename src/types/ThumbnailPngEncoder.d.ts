export interface ThumbnailPngRequest {
  id: number;
  bitmap: ImageBitmap;
}

export type ThumbnailPngResponse =
  | { id: number; blob: Blob }
  | { id: number; error: string };

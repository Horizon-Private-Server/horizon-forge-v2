import { dialog } from 'electron';
import type { BrowserWindow, OpenDialogOptions, SaveDialogOptions } from 'electron';

export function showOpenDialog(window: BrowserWindow | undefined, options: OpenDialogOptions) {
  return window ? dialog.showOpenDialog(window, options) : dialog.showOpenDialog(options);
}

export function showSaveDialog(window: BrowserWindow | undefined, options: SaveDialogOptions) {
  return window ? dialog.showSaveDialog(window, options) : dialog.showSaveDialog(options);
}

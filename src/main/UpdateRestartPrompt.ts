import { dialog } from 'electron';
import type { BrowserWindow } from 'electron';

export type UpdateRestartChoice = 'save' | 'discard' | 'cancel';

export async function showUpdateRestartPrompt(window?: BrowserWindow): Promise<UpdateRestartChoice> {
  const options = {
    type: 'warning' as const,
    title: 'Unsaved project changes',
    message: 'Save your project before installing the Forge update?',
    detail: 'Forge must close to replace the application. You can save, explicitly discard the open changes, or cancel.',
    buttons: ['Save and restart', 'Discard and restart', 'Cancel'],
    defaultId: 0,
    cancelId: 2,
    noLink: true,
  };
  const selection = window
    ? await dialog.showMessageBox(window, options)
    : await dialog.showMessageBox(options);
  return (['save', 'discard', 'cancel'] as const)[selection.response] ?? 'cancel';
}

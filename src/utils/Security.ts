import path from 'node:path';

export function isTrustedSender(senderId: number, windowWebContentsId: number | undefined): boolean {
  return windowWebContentsId !== undefined && senderId === windowWebContentsId;
}

export function isAllowedNavigation(targetUrl: string, applicationUrl: string): boolean {
  try {
    const target = new URL(targetUrl);
    const application = new URL(applicationUrl);
    target.hash = '';
    application.hash = '';
    return target.href === application.href;
  } catch {
    return false;
  }
}

export function isPathInside(root: string, candidate: string): boolean {
  const relative = path.relative(root, candidate);
  return relative === '' || (!path.isAbsolute(relative) && relative !== '..' && !relative.startsWith(`..${path.sep}`));
}

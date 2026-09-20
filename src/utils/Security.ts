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

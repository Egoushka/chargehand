// Browser notifications (the Notification API), the same events as the in-page notices. They need a secure context: https, or
// localhost. On plain http the API is not there or refuses, and everything here quietly does nothing.

export type BrowserPermission = NotificationPermission | 'unsupported';

export function browserNotificationsAvailable(): boolean {
  return typeof window !== 'undefined' && window.isSecureContext && 'Notification' in window;
}

export function browserPermission(): BrowserPermission {
  return browserNotificationsAvailable() ? Notification.permission : 'unsupported';
}

/** Asks the browser once. Call it from a click: browsers refuse a prompt the page raised on its own. */
export async function requestBrowserPermission(): Promise<BrowserPermission> {
  if (!browserNotificationsAvailable()) return 'unsupported';
  try {
    return await Notification.requestPermission();
  } catch {
    return Notification.permission;
  }
}

export interface BrowserNotice {
  title: string;
  body?: string;
  /** The same tag replaces an earlier notice instead of stacking. */
  tag: string;
  onClick?: () => void;
}

/** Shows a notice when the browser allows it. Returns whether one was shown. */
export function showBrowserNotice({ title, body, tag, onClick }: BrowserNotice): boolean {
  if (browserPermission() !== 'granted') return false;
  try {
    const notice = new Notification(title, { body, tag });
    notice.onclick = () => {
      window.focus();
      onClick?.();
      notice.close();
    };
    return true;
  } catch {
    return false; // some mobile browsers only allow a service worker to show one
  }
}

import { createContext, useContext } from 'react';

/** What a screen may ask of the shell: open a layer it owns. */
export interface ShellApi {
  openPalette: () => void;
  openShortcuts: () => void;
  /** Asks first (a confirm dialog), then halts the server. The owner's key only. */
  requestHalt: () => void;
  resumeServer: () => void;
  haltBusy: boolean;
  /** Asks the browser for notification permission. Call it from a click. */
  enableNotifications: () => void;
}

const none: ShellApi = {
  openPalette: () => {},
  openShortcuts: () => {},
  requestHalt: () => {},
  resumeServer: () => {},
  haltBusy: false,
  enableNotifications: () => {},
};

export const ShellContext = createContext<ShellApi>(none);

export const useShell = (): ShellApi => useContext(ShellContext);

import {
  createContext,
  type ReactNode,
  useCallback,
  useContext,
  useMemo,
  useRef,
  useState,
} from 'react';
import { Toast, ToastHost, type ToastProps } from '../ui';

export interface ToastSpec extends Omit<ToastProps, 'onDismiss'> {
  /** The same event twice (a poll that sees it again) shows one notice. */
  key?: string;
}

interface Entry extends ToastSpec {
  id: number;
}

interface ToastApi {
  push: (spec: ToastSpec) => void;
}

const Context = createContext<ToastApi>({ push: () => {} });

/** At most this many notices at once; a new one pushes the oldest out. */
const VISIBLE = 3;

/** Notices that stack bottom right on desktop and above the tab bar on a phone. */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [entries, setEntries] = useState<Entry[]>([]);
  const next = useRef(1);
  const remove = useCallback(
    (id: number) => setEntries((all) => all.filter((entry) => entry.id !== id)),
    [],
  );
  const push = useCallback(
    (spec: ToastSpec) =>
      setEntries((all) =>
        spec.key && all.some((entry) => entry.key === spec.key)
          ? all
          : [...all, { ...spec, id: next.current++ }].slice(-VISIBLE),
      ),
    [],
  );
  const api = useMemo(() => ({ push }), [push]);
  return (
    <ToastHost label="Notices">
      <Context.Provider value={api}>{children}</Context.Provider>
      {entries.map(({ id, key: _key, actions, ...spec }) => (
        <Toast
          key={id}
          {...spec}
          // Using an action resolves the notice: it goes with it.
          actions={actions?.map((action) => ({
            ...action,
            onSelect: () => {
              action.onSelect();
              remove(id);
            },
          }))}
          onDismiss={() => remove(id)}
        />
      ))}
    </ToastHost>
  );
}

export const useToast = (): ToastApi => useContext(Context);

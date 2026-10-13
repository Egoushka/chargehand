import { useNavigate } from '@tanstack/react-router';
import { Dialog, DialogClose, Icon, type IconName } from '../ui';
import { SCREENS } from './screens';
import { SHORTCUTS, type ShortcutGroup } from './shortcuts';

const GROUPS: ShortcutGroup[][] = [
  ['Anywhere', 'Go to'],
  ['Lists', 'On a task'],
];

/** `?`: every key that does something now. Rows come from shortcuts.ts, so the sheet never lists a key that is not wired. */
export function ShortcutsSheet({
  open,
  onOpenChange,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange} title="Keyboard shortcuts" hideTitle wide>
      <div className="ch-panel__head">
        <span className="ch-panel__title" aria-hidden="true">
          Keyboard shortcuts
        </span>
        <div className="ch-panel__aside">
          <DialogClose />
        </div>
      </div>
      <div className="pad-lg shortcuts">
        {GROUPS.map((groups) => (
          <div key={groups.join()} className="stack stack--tight">
            {groups.map((group) => (
              <section key={group} className="stack stack--tight" aria-label={group}>
                <div className="label-caps">{group}</div>
                <div className="kbd-grid">
                  {SHORTCUTS.filter((row) => row.group === group).map((row) => (
                    <div key={row.label} className="kbd-row">
                      <span className="keys">
                        {row.keys.map((key) => (
                          <kbd key={key} className="ch-kbd">
                            {key}
                          </kbd>
                        ))}
                      </span>
                      <span>{row.label}</span>
                    </div>
                  ))}
                </div>
              </section>
            ))}
            {groups.includes('Lists') ? (
              <p className="dim small">
                Cancel and halt have no bare shortcut: use the palette, then confirm.
              </p>
            ) : null}
          </div>
        ))}
      </div>
    </Dialog>
  );
}

export interface MoreAction {
  id: string;
  label: string;
  icon: IconName;
  run: () => void;
}

/** The phone's More: the screens the tab bar has no room for, then the controls the top bar has no room for. */
export function MoreSheet({
  open,
  onOpenChange,
  owner,
  actions,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  owner: boolean;
  actions: MoreAction[];
}) {
  const navigate = useNavigate();
  const rest = SCREENS.filter((s) => s.group !== 'main' && (owner || !s.ownerOnly));
  const done = (fn: () => void) => () => {
    onOpenChange(false);
    fn();
  };
  return (
    <Dialog open={open} onOpenChange={onOpenChange} title="More" placement="sheet" hideTitle>
      <div className="ch-sheet__grab" />
      <div className="sheet-list">
        {rest.map((screen) => (
          <button
            key={screen.to}
            type="button"
            className="li li--click"
            onClick={done(() => void navigate({ to: screen.to }))}
          >
            <Icon name={screen.icon} />
            <span>{screen.label}</span>
          </button>
        ))}
        {actions.map((action) => (
          <button key={action.id} type="button" className="li li--click" onClick={done(action.run)}>
            <Icon name={action.icon} />
            <span>{action.label}</span>
          </button>
        ))}
      </div>
    </Dialog>
  );
}

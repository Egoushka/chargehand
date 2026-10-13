import { createContext, type ReactNode, useContext, useEffect, useId, useRef } from 'react';
import { Button } from './Button';
import { cx } from './cx';
import { IconButton } from './IconButton';

const CloseContext = createContext<() => void>(() => {});

export interface DialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Always set: it names the dialog. `hideTitle` keeps it for assistive technology only. */
  title: string;
  hideTitle?: boolean;
  /** `center` is a dialog or the palette (near the top); `sheet` is the phone's bottom sheet. */
  placement?: 'center' | 'sheet';
  /** The class of the surface: `ch-dialog`, `ch-palette` or `ch-sheet`. */
  surface?: 'ch-dialog' | 'ch-palette' | 'ch-sheet';
  /** A confirm dialog interrupts: assistive technology announces it as an alert. */
  alert?: boolean;
  /** A width for the dialog; the design's dialogs are 480 px, the shortcuts sheet 640 px. */
  wide?: boolean;
  children: ReactNode;
}

/**
 * A modal layer: a scrim and one surface above it. It is the browser's own `<dialog>` opened with `showModal()`, so the page behind
 * is inert, focus stays inside and Esc closes it, and focus returns to what opened it. Radix's dialog does the same with a script
 * that injects a style element to hold the page's scroll, and the page's `style-src 'self'` policy refuses that element.
 * The dialog fills the viewport and is the scrim; a click on it outside the surface closes it.
 */
export function Dialog({
  open,
  onOpenChange,
  title,
  hideTitle,
  placement = 'center',
  surface = placement === 'sheet' ? 'ch-sheet' : 'ch-dialog',
  alert,
  wide,
  children,
}: DialogProps) {
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();
  const notify = useRef(onOpenChange);
  notify.current = onOpenChange;

  // Mounted only while open: opens modally, and hands the focus back when it goes.
  useEffect(() => {
    const dialog = ref.current;
    if (!open || !dialog) return;
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    if (!dialog.open) dialog.showModal();
    // The browser focuses the first thing it can, which may be a wrapper with tabindex -1; ask for the right one.
    const first =
      dialog.querySelector<HTMLElement>('[data-autofocus]') ??
      dialog.querySelector<HTMLElement>(
        'input, textarea, select, button, a[href], [tabindex]:not([tabindex="-1"])',
      );
    first?.focus({ preventScroll: true });
    return () => {
      if (dialog.open) dialog.close();
      opener?.focus({ preventScroll: true });
    };
  }, [open]);

  if (!open) return null;
  return (
    <CloseContext.Provider value={() => onOpenChange(false)}>
      <dialog
        ref={ref}
        className={cx('ch-modal', placement === 'sheet' ? 'ch-modal--sheet' : 'ch-modal--center')}
        role={alert ? 'alertdialog' : undefined}
        aria-labelledby={titleId}
        onClose={() => notify.current(false)}
        // A press on the scrim, outside the surface, closes it; Esc is the keyboard's way and the browser handles it.
        onPointerDown={(event) => {
          if (event.target === event.currentTarget) onOpenChange(false);
        }}
      >
        <div className={cx(surface, wide && 'ch-dialog--wide')}>
          <h2 id={titleId} className={hideTitle ? 'ch-sr' : 'ch-dialog__title'}>
            {title}
          </h2>
          {children}
        </div>
      </dialog>
    </CloseContext.Provider>
  );
}

/** The close button that sits in a dialog's head. */
export function DialogClose() {
  const close = useContext(CloseContext);
  return <IconButton icon="x" size="sm" label="Close" onClick={close} />;
}

export interface ConfirmDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  /** What the action does, in the dialog: "Cancel run-…1fdf01? 3 running tasks stop. Nothing is pushed." */
  text: ReactNode;
  /** The label that keeps things as they are: "Keep running". */
  keepLabel: string;
  /** The label of the action, saying what it does: "Halt server". */
  confirmLabel: string;
  onConfirm: () => void;
  busy?: boolean;
}

/** A destructive action asks first, says its effect, and offers a way out. The confirm button is the danger button. */
export function ConfirmDialog({
  open,
  onOpenChange,
  title,
  text,
  keepLabel,
  confirmLabel,
  onConfirm,
  busy,
}: ConfirmDialogProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange} title={title} alert>
      <div className="dialog__body stack stack--tight">
        <p className="ch-muted dialog__text">{text}</p>
        <div className="row row--end">
          <Button label={keepLabel} onClick={() => onOpenChange(false)} />
          <Button variant="danger" label={confirmLabel} onClick={onConfirm} loading={busy} />
        </div>
      </div>
    </Dialog>
  );
}

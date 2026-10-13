import { type ComponentProps, type ReactNode, useId } from 'react';
import { cx } from './cx';
import { Icon, type IconName } from './Icon';

export interface FieldProps {
  label: string;
  /** Receives the id to put on the control, and the `aria-describedby` that points at the hint or error. */
  children: (control: {
    id: string;
    'aria-describedby'?: string;
    'aria-invalid'?: true;
  }) => ReactNode;
  hint?: ReactNode;
  /** The error replaces the hint; it names the problem and the way out. */
  error?: ReactNode;
}

/** A label for every control, and the hint or error under it. */
export function Field({ label, children, hint, error }: FieldProps) {
  const id = useId();
  const noteId = `${id}-note`;
  const note = error ?? hint;
  return (
    <div className="ch-field">
      <label htmlFor={id}>{label}</label>
      {children({
        id,
        'aria-describedby': note ? noteId : undefined,
        'aria-invalid': error ? true : undefined,
      })}
      {note ? (
        <div
          id={noteId}
          className={error ? 'ch-error' : 'ch-hint'}
          role={error ? 'alert' : undefined}
        >
          {note}
        </div>
      ) : null}
    </div>
  );
}

type InputProps = ComponentProps<'input'> & {
  /** 36 px. */
  large?: boolean;
  /** 44 px and 16 px type: every input on a phone. */
  touch?: boolean;
  /** Paths, commits and ids. */
  mono?: boolean;
};

const inputClass = (
  { large, touch, mono }: Pick<InputProps, 'large' | 'touch' | 'mono'>,
  extra?: string,
) =>
  cx(
    'ch-input',
    large && 'ch-input--lg',
    touch && 'ch-input--touch',
    mono && 'ch-input--mono',
    extra,
  );

export function Input({ large, touch, mono, className, ...rest }: InputProps) {
  return <input {...rest} className={inputClass({ large, touch, mono }, className)} />;
}

export function Textarea({
  touch,
  mono,
  className,
  ...rest
}: ComponentProps<'textarea'> & { touch?: boolean; mono?: boolean }) {
  return <textarea {...rest} className={inputClass({ touch, mono }, className)} />;
}

/** A native select: the design's popover select waits for a screen that needs more than a short list. */
export function Select({
  large,
  touch,
  className,
  children,
  ...rest
}: ComponentProps<'select'> & { large?: boolean; touch?: boolean }) {
  return (
    <select {...rest} className={inputClass({ large, touch }, cx('ch-select-native', className))}>
      {children}
    </select>
  );
}

/** An input with an icon inside it, for a filter or a search. */
export function InputGroup({ icon, children }: { icon: IconName; children: ReactNode }) {
  return (
    <div className="ch-input-group">
      <Icon name={icon} />
      {children}
    </div>
  );
}

export function Check({ className, ...rest }: Omit<ComponentProps<'input'>, 'type'>) {
  return <input {...rest} type="checkbox" className={cx('ch-check', className)} />;
}

/** A switch is a checkbox with the switch role. It needs a label of its own. */
export function Switch({ className, ...rest }: Omit<ComponentProps<'input'>, 'type' | 'role'>) {
  // biome-ignore lint/a11y/useAriaPropsForRole: a checkbox with the switch role takes its state from `checked`
  return <input {...rest} type="checkbox" role="switch" className={cx('ch-switch', className)} />;
}

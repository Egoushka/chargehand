import type { ComponentProps, ReactNode } from 'react';
import { cx } from './cx';
import { Icon, type IconName } from './Icon';

export interface ButtonStyle {
  /** `primary` (accent) is the one main action of a view; `danger` is for Halt server and a cancel dialog's confirm. */
  variant?: 'primary' | 'quiet' | 'danger' | 'danger-quiet';
  size?: 'sm' | 'lg' | 'touch';
  block?: boolean;
}

/** The class list of a button, for an anchor or a router link that must look like one. */
export function buttonClass({ variant, size, block }: ButtonStyle = {}, extra?: string): string {
  return cx(
    'ch-btn',
    variant && `ch-btn--${variant}`,
    size && `ch-btn--${size}`,
    block && 'ch-btn--block',
    extra,
  );
}

export interface ButtonProps extends ButtonStyle, Omit<ComponentProps<'button'>, 'children'> {
  /** A verb-first label that names the object: "Cancel batch", never "OK". */
  label: ReactNode;
  icon?: IconName;
  /** A key hint shown after the label. */
  kbd?: string;
  /** A request is in flight: the label gives way to a spinner of the same width. */
  loading?: boolean;
}

export function Button({
  label,
  variant,
  size,
  block,
  icon,
  kbd,
  loading,
  type = 'button',
  className,
  disabled,
  ...rest
}: ButtonProps) {
  return (
    <button
      {...rest}
      type={type}
      className={buttonClass({ variant, size, block }, cx(loading && 'is-loading', className))}
      disabled={disabled}
      aria-busy={loading || undefined}
    >
      {icon ? <Icon name={icon} /> : null}
      {label}
      {kbd ? <kbd className="ch-kbd">{kbd}</kbd> : null}
    </button>
  );
}

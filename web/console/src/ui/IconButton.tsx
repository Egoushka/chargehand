import type { ComponentProps } from 'react';
import { cx } from './cx';
import { Icon, type IconName } from './Icon';

export interface IconButtonProps extends Omit<ComponentProps<'button'>, 'children'> {
  icon: IconName;
  /** Names the action; it is the accessible name and the title. */
  label: string;
  size?: 'sm' | 'lg' | 'touch';
  outline?: boolean;
  /** The on state of a toggle (rail collapsed, stream paused). */
  pressed?: boolean;
}

export function IconButton({
  icon,
  label,
  size,
  outline,
  pressed,
  type = 'button',
  className,
  ...rest
}: IconButtonProps) {
  return (
    <button
      title={label}
      {...rest}
      type={type}
      aria-label={label}
      aria-pressed={pressed}
      className={cx(
        'ch-ibtn',
        size && `ch-ibtn--${size}`,
        outline && 'ch-ibtn--outline',
        className,
      )}
    >
      <Icon name={icon} />
    </button>
  );
}

import * as RadixMenu from '@radix-ui/react-dropdown-menu';
import type { ReactNode } from 'react';
import { cx } from './cx';
import { Icon, type IconName } from './Icon';

export interface MenuItem {
  id: string;
  label: string;
  icon?: IconName;
  onSelect: () => void;
  /** A destructive item is tinted and says so in its label. */
  danger?: boolean;
}

/**
 * A menu that opens under a button. It is not modal: nothing else on the page is locked while it is open, which also keeps
 * the page's style-src policy out of it (a modal layer would inject a style element to hold the page's scroll).
 */
export function Menu({
  trigger,
  items,
  header,
  align = 'end',
}: {
  /** The button; it must forward a ref and props, as `IconButton` and `Button` do. */
  trigger: ReactNode;
  items: MenuItem[];
  /** Read-only text above the items (who is signed in). */
  header?: ReactNode;
  align?: 'start' | 'end';
}) {
  return (
    <RadixMenu.Root modal={false}>
      <RadixMenu.Trigger asChild>{trigger}</RadixMenu.Trigger>
      <RadixMenu.Portal>
        <RadixMenu.Content className="ch-menu" align={align} sideOffset={6}>
          {header ? <div className="ch-menu__head">{header}</div> : null}
          {items.map((item) => (
            <RadixMenu.Item
              key={item.id}
              className={cx('ch-menu__item', item.danger && 'ch-menu__item--danger')}
              onSelect={item.onSelect}
            >
              {item.icon ? <Icon name={item.icon} /> : null}
              {item.label}
            </RadixMenu.Item>
          ))}
        </RadixMenu.Content>
      </RadixMenu.Portal>
    </RadixMenu.Root>
  );
}

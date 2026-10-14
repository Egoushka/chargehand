import type { ReactNode } from 'react';
import { Icon, type IconName } from '../ui';

/** What a chart panel shows when it has nothing: the design system's empty state at the height of a panel row. */
export function TightEmpty({
  title,
  text,
  icon = 'inbox',
}: {
  title: string;
  text: ReactNode;
  icon?: IconName;
}) {
  return (
    <div className="ch-empty ch-empty--tight">
      <div className="ch-empty__mark">
        <Icon name={icon} />
      </div>
      <h3 className="ch-empty__title">{title}</h3>
      <p className="ch-empty__text">{text}</p>
    </div>
  );
}

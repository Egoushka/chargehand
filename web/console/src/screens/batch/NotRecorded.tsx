import { Icon } from '../../ui';

/** What the server does not keep, in its own words (`not_recorded` of the contract): said where the screen would otherwise look complete. */
export function NotRecorded({
  notes,
  title = 'Not recorded',
}: {
  notes: readonly string[];
  title?: string;
}) {
  if (notes.length === 0) return null;
  return (
    <div className="notrecorded" role="note">
      <Icon name="info" />
      <div className="stack stack--tight">
        <div className="b">{title}</div>
        <ul className="notrecorded__list">
          {notes.map((note) => (
            <li key={note}>{note}</li>
          ))}
        </ul>
      </div>
    </div>
  );
}

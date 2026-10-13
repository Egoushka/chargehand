import { EmptyState } from '../ui';

/** A screen that is planned and not built: its name, what it will do, and nothing that looks like data. */
export function Placeholder({ title, blurb }: { title: string; blurb: string }) {
  return (
    <div className="screen">
      <div className="screen__head">
        <div>
          <h1 className="screen__title">{title}</h1>
          <p className="screen__sub">{blurb}</p>
        </div>
      </div>
      <div className="card">
        <EmptyState
          icon="timeline"
          title="Not built yet"
          text="This screen is planned. Until it lands, the same information is in the routes of the server guide."
        />
      </div>
    </div>
  );
}

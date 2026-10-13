export function Placeholder({ title, blurb }: { title: string; blurb: string }) {
  return (
    <section>
      <h1>{title}</h1>
      <p>{blurb}</p>
      <p className="muted">Not built yet.</p>
    </section>
  );
}

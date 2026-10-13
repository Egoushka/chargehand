namespace Chargehand.Modules.Journal.Api;

/// <summary>Names one side effect of a driven batch in the journal (ADR 0061): the batch is the flow, and <paramref name="Name"/> says what the effect acts on. A key must be the same
/// after a restart, so it is built from ids that are saved with the batch and never from a counter.</summary>
/// <param name="Batch">The batch's run id.</param>
/// <param name="Name">What the step acts on, e.g. <c>prep/&lt;run&gt;</c>.</param>
public readonly record struct StepKey(string Batch, string Name)
{
    /// <summary>The workspace helper of a session run (<c>chargehand-prep-&lt;run&gt;</c>).</summary>
    public static StepKey Prep(string batch, string run) => new(batch, $"prep/{run}");

    /// <summary>A write into a run's output volume (<c>chargehand-xfer-&lt;run&gt;</c>); <paramref name="file"/> is the file's fixed name, so a restarted task finds the same key.</summary>
    public static StepKey Transfer(string batch, string run, string file) => new(batch, $"xfer/{run}/{file}");

    public override string ToString() => $"{Batch}: {Name}";
}

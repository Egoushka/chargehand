namespace Chargehand.Modules.Journal.Api;

/// <summary>Names one side effect of a driven batch in the journal (ADR 0061): the batch is the flow, and <paramref name="Name"/> says what the effect acts on. A key must be the same
/// after a restart, so it is built from ids that are saved with the batch and never from a counter.</summary>
/// <param name="Batch">The batch's run id.</param>
/// <param name="Name">What the step acts on, e.g. <c>prep/&lt;run&gt;</c>.</param>
public readonly record struct StepKey(string Batch, string Name)
{
    /// <summary>The workspace helper of a session run (<c>chargehand-prep-&lt;run&gt;</c>).</summary>
    public static StepKey Prep(string batch, string run) => new(batch, $"prep/{run}");

    /// <summary>The workspace helper of a run's verification (ADR 0039): the same work volume as <see cref="Prep"/>, removed and prepared again for each verification, so its key is reset first.</summary>
    public static StepKey VerifyPrep(string batch, string run) => new(batch, $"prep/{run}/verify");

    /// <summary>A write into a run's output volume (<c>chargehand-xfer-&lt;run&gt;</c>); <paramref name="file"/> is the file's fixed name, so a restarted task finds the same key.</summary>
    public static StepKey Transfer(string batch, string run, string file) => new(batch, $"xfer/{run}/{file}");

    /// <summary>A task's session start under one run id (<c>chargehand-&lt;run&gt;</c>): the output volume, the workspace, the task file and the container. The run id is the one saved for the
    /// task before the start, so a restarted server finds the same key.</summary>
    public static StepKey Session(string batch, string task, string run) => new(batch, $"session/{task}/{run}");

    /// <summary>The verification of the branch a session run made, by the run that is handed over: its recorded verdict stands for that run's bundle, so a resumed handover does not run it again.</summary>
    public static StepKey Verify(string batch, string run) => new(batch, $"handover/{run}/verify");

    /// <summary>The push of the branch a session run made, by the run that is handed over; a CI-fix session has its own run id, so its own key.</summary>
    public static StepKey Push(string batch, string run) => new(batch, $"handover/{run}/push");

    /// <summary>The draft pull request for the branch a session run made (see <see cref="Push"/>).</summary>
    public static StepKey PullRequest(string batch, string run) => new(batch, $"handover/{run}/pr");

    public override string ToString() => $"{Batch}: {Name}";
}

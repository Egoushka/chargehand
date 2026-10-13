namespace Chargehand.Modules.Journal.Api;

/// <summary>What a step is told when it runs.</summary>
/// <param name="Number">1 for the first run of the key, one more for each run after it.</param>
/// <param name="Resumed">An earlier attempt recorded its intent and never its outcome (a crash, a kill, a cancel): whatever it started may exist, so the step adopts it or removes it before it acts.</param>
public readonly record struct StepAttempt(int Number, bool Resumed)
{
    /// <summary>An earlier attempt ran, whether it was interrupted or failed.</summary>
    public bool Repeated => Number > 1;
}

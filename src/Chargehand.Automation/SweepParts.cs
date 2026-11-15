using Chargehand.Modules.Intake.Api;

namespace Chargehand.Automation;

/// <summary>What the sweep shares with GitHub intake, built where intake is: the server adds what only it has (the run service, the task pool, the event log) and composes the sweep.</summary>
/// <param name="Trusted">The authors whose issues the sweep takes: <c>allowed_authors</c> and the integrator App's login.</param>
/// <param name="Directory">Where the sweep keeps its state (<c>intake/</c>, beside <c>github.json</c>).</param>
public sealed record SweepParts(IIntakeHost Host, IRepositoryHeads Heads, IntakeState State, IReadOnlyDictionary<string, AutomationRepository> Repositories, IReadOnlySet<string> Trusted,
    ISweepSorter Sorter, SweepSettings Settings, string Directory);

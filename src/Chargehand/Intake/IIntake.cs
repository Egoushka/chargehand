using Chargehand.Contracts;

namespace Chargehand.Intake;

/// <summary>Turns a request into a Task Spec: deterministic checks, then one schema-validated model call (ADR 0005).</summary>
public interface IIntake
{
    Task<TaskSpec> CreateSpecAsync(RunRequest request, CancellationToken ct);
}

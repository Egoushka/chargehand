using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Modules.Runners.Api;

/// <summary>When the host has room for another session (ADR 0045). A task starts only if available memory minus <c>ReserveMb</c> covers its memory request, the working set
/// that sessions of its repository have measured (not the container's limit), and memory pressure is low; a running task is never stopped for it.</summary>
/// <param name="ReserveMb">Memory left for everything else on the host.</param>
/// <param name="MaxLoadPerCpu">A one-minute load per processor above this holds new starts; null: no load rule.</param>
/// <param name="DefaultRequestMb">The request of a task whose repository has too few recorded peaks, and no other repository does either. Clamped to 512 MiB and the container limit.</param>
/// <param name="MaxMemoryPressure">Linux pressure stall information: new starts wait while <c>some avg10</c> of <c>/proc/pressure/memory</c> is above this percentage. A host without it skips the rule; 100 turns it off.</param>
public sealed record DrivenAdmission(int ReserveMb = 1024, double? MaxLoadPerCpu = null, int DefaultRequestMb = 1536, double MaxMemoryPressure = 10.0)
{
    /// <summary>The least a task's request may be, whatever the history says (MiB).</summary>
    public const int MinRequestMb = 512;

    /// <summary>Throws an invalid_request error naming the key when a value cannot work.</summary>
    public void Validate()
    {
        var problems = new List<string>();
        if (ReserveMb < 0) problems.Add("reserve_mb must be at least 0");
        if (MaxLoadPerCpu is <= 0) problems.Add("max_load_per_cpu must be above 0");
        if (DefaultRequestMb < MinRequestMb) problems.Add($"default_request_mb must be at least {MinRequestMb}");
        if (MaxMemoryPressure is <= 0 or > 100) problems.Add("max_memory_pressure must be above 0 and at most 100");
        if (problems.Count > 0)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"driven.admission: {string.Join("; ", problems)}", "Fix driven.admission in the profile; docs/guide/driven.md lists the keys.");
    }
}

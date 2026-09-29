namespace Chargehand.Runtime;

/// <summary>
/// Optional on a runtime that hands granted services to workers (ADR 0034): whether a session's worker got them. A grant is
/// resolved before the worker starts, so a server can still fail to connect when the worker does; the orchestrator asks after
/// a node ends and records the answer on the run instead of letting the worker go without the tools unnoticed.
/// </summary>
public interface IServiceHealth
{
    /// <summary>The granted servers the session's worker could not use, by server name, with what the runtime saw (the CLI's status
    /// such as <c>failed</c>, or <c>absent</c> when it did not list the server). Empty when all connected, or before the session has run.</summary>
    IReadOnlyDictionary<string, string> UnavailableServices(string sessionId);
}

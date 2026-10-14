using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Modules.Runners.Api;

/// <summary>A runner that did not answer: unreachable, silent past the client's timeout, or a gateway error on the way (ADR 0058). The same call may succeed later, which is what a runner on a
/// laptop that sleeps or a path that flaps needs; a refusal (a key, an allowlist, a body) is a plain <see cref="ChargehandException"/> and is not waited out.</summary>
public sealed class RunnerUnreachableException(string message) : ChargehandException(ErrorCode.ContainerUnavailable, message, "Check that the runner service is running and reachable, then retry.");

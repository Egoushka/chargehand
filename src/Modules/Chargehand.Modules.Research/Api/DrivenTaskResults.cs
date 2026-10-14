using Chargehand.Contracts;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Research.Api;

/// <summary>P4 (<see cref="ITaskResults"/>): <see cref="TaskResultBuilder.BuildAsync"/> with the server's evidence resolver and support check, and its signer. The host wires the
/// resolver and support check implementations, which stay outside Research until C5.</summary>
public sealed class DrivenTaskResults(IEvidenceResolver resolver, Func<ResultContract, EvidenceScope, CancellationToken, Task<ResultContract>>? supportCheck,
    Func<ResultContract, ResultContract>? sign) : ITaskResults
{
    public Task<ResultContract> BuildAsync(DrivenTaskInput input, CancellationToken ct) => TaskResultBuilder.BuildAsync(input, resolver, supportCheck, ct);

    public ResultContract Sign(ResultContract result) => sign is null ? result : sign(result);
}

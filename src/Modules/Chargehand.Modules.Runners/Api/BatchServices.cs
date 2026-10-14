using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Modules.Runners.Api;

/// <summary>A batch's test services (ADR 0048): started on the batch network after it exists, for the batch's lifetime, and removed before the network is. A service that cannot start
/// ends the batch before any session does, and what was started is removed.</summary>
public sealed class BatchServices(IContainerEngine engine)
{
    /// <summary>The value of the session's <see cref="ContainerTemplate.ServicesVariable"/>: <c>name=name:port</c> pairs.</summary>
    public static string Addresses(IEnumerable<ServiceSpec> services) => string.Join(',', services.Select(s => $"{s.Name}={s.Name}:{s.Port}"));

    /// <returns>The container ids, in the order of <paramref name="services"/>.</returns>
    /// <exception cref="ChargehandException">The engine cannot start services, or one did not start.</exception>
    public Task<IReadOnlyList<string>> StartAsync(IReadOnlyList<ServiceSpec> services, CancellationToken ct) => BuildAsync(services, resume: false, ct);

    /// <summary>After a server restart (ADR 0043): a service that still runs is kept, one that is gone is started again.</summary>
    public Task<IReadOnlyList<string>> EnsureAsync(IReadOnlyList<ServiceSpec> services, CancellationToken ct) => BuildAsync(services, resume: true, ct);

    public async Task RemoveAsync(IReadOnlyList<ServiceSpec> services, CancellationToken ct)
    {
        foreach (var service in services)
            await engine.RemoveAsync(service.ContainerName, ct);
    }

    private async Task<IReadOnlyList<string>> BuildAsync(IReadOnlyList<ServiceSpec> services, bool resume, CancellationToken ct)
    {
        if (services.Count == 0)
            return [];
        if (engine is not IServiceEngine starter)
            throw new ChargehandException(ErrorCode.ContainerUnavailable, "this deployment's container engine cannot start test services",
                "Use a runner built from this release (driven.runner) or Docker on this machine, or remove the repository from driven.services.repositories.");
        List<string> ids = [];
        try
        {
            foreach (var service in services)
            {
                if (resume && (await engine.InspectAsync(service.ContainerName, ct)).Status == ContainerStatus.Running)
                {
                    ids.Add(service.ContainerName);
                    continue;
                }
                await engine.RemoveAsync(service.ContainerName, ct);
                ids.Add(await starter.StartServiceAsync(service, ct));
            }
            return ids;
        }
        catch
        {
            await RemoveAsync(services, CancellationToken.None);
            throw;
        }
    }
}

using Chargehand.Config;
using Chargehand.Modules.Handover.Api;

namespace Chargehand.Driven;

/// <summary><c>Off</c>: chargehand leaves the draft pull request alone (ADR 0039). <c>Ask</c>: it is marked ready and commented on, and a person merges. <c>Merge</c>: it is merged through the
/// repository's required checks.</summary>
public enum IntegrationMode { Off, Ask, Merge }

/// <summary>Which repositories the integrator may act on, and how (ADR 0046). Pure: a repository is off unless the profile lists it, and <c>always_ask</c> wins over everything.</summary>
public sealed class IntegratorPolicy(DrivenIntegrator config)
{
    private readonly HashSet<string> _alwaysAsk = new(config.AlwaysAsk ?? [], StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _repositories = new(config.Repositories ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);

    public IntegrationMode Resolve(RemoteRepository repository)
    {
        var name = $"{repository.Owner}/{repository.Name}";
        if (_alwaysAsk.Contains(name))
            return IntegrationMode.Ask;
        return _repositories.GetValueOrDefault(name) switch
        {
            "merge" => IntegrationMode.Merge,
            "ask" => IntegrationMode.Ask,
            _ => IntegrationMode.Off,
        };
    }
}

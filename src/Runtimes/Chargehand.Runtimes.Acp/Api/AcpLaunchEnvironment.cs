using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Runtimes.Acp.Api;

/// <summary>The variables an agent is started with beyond the sandbox's own cut environment (ADR 0086, decision 9): its fixed ones, the key and
/// the gateway. Nothing else reaches it, and its home is a private directory, so no stored subscription login can stand in for a missing key.</summary>
public static class AcpLaunchEnvironment
{
    /// <param name="secret">Resolves a secret item name to its value; throws <see cref="InvalidOperationException"/> when it does not.</param>
    /// <exception cref="ChargehandException"><c>credential_unavailable</c> without a key secret or when it does not resolve; <c>invalid_request</c> for a gateway the agent cannot be pointed at.</exception>
    public static IReadOnlyDictionary<string, string> Build(AcpAgentSpec agent, AcpAgentCredential? credential, Func<string, string> secret)
    {
        if (string.IsNullOrWhiteSpace(credential?.ApiKeySecret))
            throw new ChargehandException(ErrorCode.CredentialUnavailable, $"acp.agents.{agent.Id}.api_key_secret is not set: ACP agents run on an API key or a gateway's key, never a login",
                $"Name the secret item that holds the key in the profile's acp.agents.{agent.Id}.api_key_secret.");
        string key;
        try
        {
            key = secret(credential.ApiKeySecret);
        }
        catch (InvalidOperationException)
        {
            throw new ChargehandException(ErrorCode.CredentialUnavailable, $"the secret '{credential.ApiKeySecret}' (acp.agents.{agent.Id}.api_key_secret) does not resolve",
                "Create the item in the secret store the profile's secrets list reads.");
        }

        var env = new Dictionary<string, string>(agent.Env ?? new Dictionary<string, string>(), StringComparer.Ordinal) { [agent.ApiKeyEnv] = key };
        if (credential.BaseUrl is { Length: > 0 } baseUrl)
        {
            env[agent.BaseUrlEnv ?? throw new ChargehandException(ErrorCode.InvalidRequest, $"acp.agents.{agent.Id}.base_url is set, but {agent.Name} cannot be pointed at a gateway",
                $"Remove base_url, or give the agent a base_url_env in the pinned list after checking that it honours one.")] = baseUrl;
        }

        return env;
    }
}

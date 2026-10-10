using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Runner;

/// <summary>The runner's request bodies. Each is exactly the fields the runner acts on; a body with any other field is refused, so a caller
/// cannot ask for a flag, a mount, a capability or a user by adding one.</summary>
public sealed record RunnerStart(string RunId, string Image, string WorkVolume, string OutVolume, string Network,
    Dictionary<string, string> Env, int MemoryMb, double Cpus, int Pids, List<string> Command);

public sealed record RunnerSignal(string Id, string Signal);

public sealed record RunnerVolume(string Name, string RunId);

public sealed record RunnerNetwork(string Name, string BatchId);

public sealed record RunnerConnect(string Container, string Network);

/// <summary>The image is the runner's, not the caller's.</summary>
/// <param name="Gateway">The model endpoint's secrets, which the runner writes to a private env file for the egress container and never logs.</param>
public sealed record RunnerEgress(string BatchId, string Network, List<string> Allow, List<string>? Forwards = null, RunnerModelGateway? Gateway = null);

/// <summary>What the egress container needs to exchange run tokens for the model credential (ADR 0039, decision 9).</summary>
public sealed record RunnerModelGateway(string Credential, string TokenKey, string Host, int Port);

/// <summary>A workspace to prepare; the runner builds the helper's command line from <see cref="Chargehand.Containers.ContainerTemplate.WorkspaceArgs"/>.</summary>
public sealed record RunnerWorkspace(string RunId, string Image, string SourcePath, string WorkVolume, string Branch, string Commit);

public sealed record RunnerInspect(string Status, int? ExitCode, bool OomKilled);

public static class RunnerJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Modules.Runners.Api;

/// <summary>The runner's request bodies. Each is exactly the fields the runner acts on; a body with any other field is refused, so a caller
/// cannot ask for a flag, a mount, a capability or a user by adding one.</summary>
/// <param name="Caches">Package cache volumes (ADR 0048); left out of the body when there are none, so a runner older than this field still accepts the call.</param>
/// <param name="AllowedExtraEnv">The batch's declared <c>session_env</c> names (ADR 0048), which <paramref name="Env"/> may also use; left out of the body
/// when there are none, so a runner older than this field still accepts the call.</param>
public sealed record RunnerStart(string RunId, string Image, string WorkVolume, string OutVolume, string Network,
    Dictionary<string, string> Env, int MemoryMb, double Cpus, int Pids, List<string> Command, List<string>? Caches = null, List<string>? AllowedExtraEnv = null);

/// <summary>A test service of a batch (ADR 0048); the runner builds the command line from <see cref="Chargehand.Modules.Runners.Api.ContainerTemplate.ServiceArgs"/>.</summary>
public sealed record RunnerService(string BatchId, string Name, string Image, string Network, int Port, Dictionary<string, string> Env, List<string> Tmpfs, int MemoryMb);

public sealed record RunnerSignal(string Id, string Signal);

public sealed record RunnerVolume(string Name, string RunId);

public sealed record RunnerNetwork(string Name, string BatchId);

public sealed record RunnerConnect(string Container, string Network);

/// <summary>The image is the runner's, not the caller's.</summary>
/// <param name="Gateway">The model endpoint's secrets, which the runner writes to a private env file for the egress container and never logs.</param>
public sealed record RunnerEgress(string BatchId, string Network, List<string> Allow, List<string>? Forwards = null, RunnerModelGateway? Gateway = null);

/// <summary>What the egress container needs to exchange run tokens for the model credential (ADR 0039, decision 9).</summary>
public sealed record RunnerModelGateway(string Credential, string TokenKey, string Host, int Port);

/// <summary>A workspace to prepare; the runner builds the helper's command line from <see cref="Chargehand.Modules.Runners.Api.ContainerTemplate.WorkspaceArgs"/>.</summary>
public sealed record RunnerWorkspace(string RunId, string Image, string SourcePath, string WorkVolume, string Branch, string Commit);

/// <summary>A workspace to prepare from <c>source.bundle</c> in the run's output volume (ADR 0058); the runner derives the volume's name from the run id and builds the helper's command line from
/// <see cref="Chargehand.Modules.Runners.Api.ContainerTemplate.BundleWorkspaceArgs"/>.</summary>
public sealed record RunnerBundleWorkspace(string RunId, string Image, string WorkVolume, string Branch, string Commit);

public sealed record RunnerResources(List<string> Containers, List<string> Networks, List<string> Volumes);

/// <param name="MemoryPeakMb">Left out of the reply when the runner's engine could not measure it, so a server older than this field still reads the reply.</param>
public sealed record RunnerInspect(string Status, int? ExitCode, bool OomKilled, int? MemoryPeakMb = null);

public static class RunnerJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed record IdReply(string Id);

internal sealed record LogsReply(string Logs);

internal sealed record CountReply(int Count);

/// <summary>The body of a call that carries no fields: <c>{}</c>.</summary>
internal sealed record RunnerEmpty;

/// <summary>The runner client's bodies and replies without reflection (ADR 0060), with the options of <see cref="RunnerJson.Options"/>, which the runner server still uses.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RunnerStart))]
[JsonSerializable(typeof(RunnerService))]
[JsonSerializable(typeof(RunnerSignal))]
[JsonSerializable(typeof(RunnerVolume))]
[JsonSerializable(typeof(RunnerNetwork))]
[JsonSerializable(typeof(RunnerConnect))]
[JsonSerializable(typeof(RunnerEgress))]
[JsonSerializable(typeof(RunnerWorkspace))]
[JsonSerializable(typeof(RunnerBundleWorkspace))]
[JsonSerializable(typeof(RunnerResources))]
[JsonSerializable(typeof(RunnerInspect))]
[JsonSerializable(typeof(HostSnapshot))]
[JsonSerializable(typeof(IdReply))]
[JsonSerializable(typeof(LogsReply))]
[JsonSerializable(typeof(CountReply))]
[JsonSerializable(typeof(RunnerEmpty))]
internal sealed partial class RunnerJsonContext : JsonSerializerContext;

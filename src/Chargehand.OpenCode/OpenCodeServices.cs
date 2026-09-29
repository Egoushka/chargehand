using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Runtime;

namespace Chargehand.OpenCode;

/// <param name="Connect">How long a registered server may take to connect.</param>
/// <param name="Poll">How often its status is read meanwhile; the spike saw a stdio server connect within about 0.5 s (O1).</param>
/// <param name="Settle">How long to wait once it reads connected. OpenCode reports the status before the server's tools can be
/// called: a probe with a stand-in model saw the first call succeed 140 to 205 ms later (2.0.19, local stdio server). A worker
/// asking for a tool in that gap is told it does not exist.</param>
internal sealed record ServiceTimings(TimeSpan Connect, TimeSpan Poll, TimeSpan Settle)
{
    public static ServiceTimings Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500));
}

/// <summary>
/// The MCP servers a run's grants name, registered with the OpenCode server at the run's location (ADR 0034). What the
/// spike found (ADR 0034, O1 to O7) decides the lifecycle:
/// <list type="bullet">
/// <item>The registry belongs to a location and every session there sees a server (O2), and <c>DELETE</c> takes it from
/// running sessions at once (O7). So a server is removed only when no run holds it: a registration is counted per holder
/// and the last one out removes it.</item>
/// <item>Which tools a session may call is its own ruleset (O5, O6), and the <c>PUT</c> does not depend on it. So runs
/// with different grants of one server share a registration and each session gets its own allow rules.</item>
/// <item>What a registration is, is the config it was made with. The name is the profile's server name and a keyed hash of
/// the config, so a run with other credentials gets another server instead of changing the first one's, and two chargehand
/// processes sharing one OpenCode server do not meet in the same name. The key is per process, so the name reveals nothing
/// about a credential.</item>
/// <item>A registration lives as long as the run, not the node: the nodes of a split run share it, and a fork made after its
/// parent ended still finds it. The orchestrator ends the run (<see cref="IRunCleanup"/>), whatever became of its nodes.</item>
/// <item>The <c>PUT</c> returns before the server connects (O1), so <see cref="GrantAsync"/> polls until it does. One that
/// does not is dropped from the grant, removed, and reported through <see cref="IServiceHealth"/>.</item>
/// </list>
/// </summary>
internal sealed class OpenCodeServices(IOpenCodeClient oc, ServiceTimings timings)
{
    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromSeconds(10);

    private const int MaxReason = 300;

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(16);
    private readonly object _gate = new();
    private readonly Dictionary<(string Directory, string Name), Registration> _registrations = [];
    private readonly Dictionary<string, Run> _runs = [];
    private readonly Dictionary<string, SessionServices> _sessions = [];

    /// <summary>What a node's grants came to: the rules that let its session call the connected servers' granted tools, and the servers that did not connect.</summary>
    public sealed record Granted(string? RunId, IReadOnlyList<PermissionRule> Rules, IReadOnlyDictionary<string, string> Unavailable)
    {
        public static Granted None { get; } = new(null, [], new Dictionary<string, string>());
    }

    private sealed record SessionServices(string RunId, IReadOnlyDictionary<string, string> Unavailable);

    private sealed record Lease(string? Name, Registration? Registration, string? Failure);

    private sealed class Run
    {
        public Dictionary<(string Directory, string Server), Lazy<Task<Lease>>> Leases { get; } = [];

        public List<string> Sessions { get; } = [];
    }

    private sealed class Registration(string server, string directory, string name, McpConfigBody config)
    {
        public string Server { get; } = server;

        public string Directory { get; } = directory;

        public string Name { get; } = name;

        public McpConfigBody Config { get; } = config;

        /// <summary>Guarded by the gate.</summary>
        public int Holders { get; set; }

        /// <summary>Set, under the gate, by the holder that takes the count to zero; completes once the server is removed.</summary>
        public TaskCompletionSource? Closing { get; set; }

        /// <summary>Null when the server connected, else why it did not. Completes once, whatever happens.</summary>
        public TaskCompletionSource<string?> Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The last status seen while waiting, for the message when the wait runs out.</summary>
        public string Last { get; set; } = "absent";
    }

    public async Task<Granted> GrantAsync(NodeSpec spec, CancellationToken ct)
    {
        if (spec.Services is not { Count: > 0 } services)
            return Granted.None;
        if (!spec.Metadata.TryGetValue(IRunCleanup.RunMetadataKey, out var runId))
            throw new ArgumentException($"a node with services carries its run's id in metadata '{IRunCleanup.RunMetadataKey}': the run's servers are removed when it ends", nameof(spec));
        var pending = services.Where(g => g.Tools.Count > 0).Select(g => (Grant: g, Lease: LeaseOf(runId, spec.Directory, g))).ToList();
        var rules = new List<PermissionRule>();
        var unavailable = new Dictionary<string, string>();
        foreach (var (grant, task) in pending)
        {
            var lease = await task.WaitAsync(ct);
            if (lease.Name is null)
                unavailable[grant.Server] = lease.Failure!;
            else
                rules.AddRange(grant.Tools.Select(t => new PermissionRule($"{lease.Name}_{ActionPart(t)}", "*", PermissionEffect.Allow)));
        }
        return new Granted(runId, rules, unavailable);
    }

    public void Bind(string sessionId, Granted granted)
    {
        if (granted.RunId is null)
            return;
        lock (_gate)
            if (_runs.TryGetValue(granted.RunId, out var run))
            {
                _sessions[sessionId] = new SessionServices(granted.RunId, granted.Unavailable);
                run.Sessions.Add(sessionId);
            }
    }

    /// <summary>A fork has its parent's rules and location, so it has its parent's servers and its parent's unavailable ones.</summary>
    public void Inherit(string parentId, string forkId)
    {
        lock (_gate)
            if (_sessions.TryGetValue(parentId, out var parent) && _runs.TryGetValue(parent.RunId, out var run))
            {
                _sessions[forkId] = parent;
                run.Sessions.Add(forkId);
            }
    }

    public IReadOnlyDictionary<string, string> Unavailable(string sessionId)
    {
        lock (_gate)
            return _sessions.TryGetValue(sessionId, out var s) ? s.Unavailable : new Dictionary<string, string>();
    }

    /// <summary>Lets go of every server the run held; the ones no other run holds are removed. Safe to repeat; never throws.</summary>
    public async Task EndRunAsync(string runId, CancellationToken ct)
    {
        Run? run;
        lock (_gate)
        {
            if (!_runs.Remove(runId, out run))
                return;
            foreach (var id in run.Sessions)
                _sessions.Remove(id);
        }
        var leases = new List<Lease>();
        foreach (var lazy in run.Leases.Values)
            try
            {
                leases.Add(await lazy.Value);
            }
            catch (Exception)
            {
                // A lease that faulted (a transport OpenCode cannot take) registered nothing.
            }
        await Task.WhenAll(leases.Where(l => l.Registration is not null).Select(l => Release(l.Registration!, ct)));
    }

    private Task<Lease> LeaseOf(string runId, string directory, ServiceGrant grant)
    {
        Lazy<Task<Lease>> lease;
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var run))
                _runs[runId] = run = new Run();
            if (!run.Leases.TryGetValue((directory, grant.Server), out lease!))
                run.Leases[(directory, grant.Server)] = lease = new Lazy<Task<Lease>>(() => Acquire(directory, grant));
        }
        return lease.Value;
    }

    private async Task<Lease> Acquire(string directory, ServiceGrant grant)
    {
        var config = ConfigOf(grant.Transport);
        var name = $"{grant.Server}-{Tag(config)}";
        var registration = await Join(grant.Server, directory, name, config);
        if (await registration.Connected.Task is not { } failure)
            return new Lease(name, registration, null);
        // Nothing to keep for a server that did not connect: the next run tries it afresh.
        await Release(registration, CancellationToken.None);
        return new Lease(null, null, failure);
    }

    /// <summary>Takes a hold on the registration, making it when there is none. One being removed is waited for, so the
    /// <c>DELETE</c> of the old one cannot land after the <c>PUT</c> of the new one.</summary>
    private async Task<Registration> Join(string server, string directory, string name, McpConfigBody config)
    {
        while (true)
        {
            Registration registration;
            Task? removing = null;
            var created = false;
            lock (_gate)
            {
                if (_registrations.TryGetValue((directory, name), out registration!))
                {
                    if (registration.Closing is { } closing)
                        removing = closing.Task;
                    else
                        registration.Holders++;
                }
                else
                {
                    _registrations[(directory, name)] = registration = new Registration(server, directory, name, config) { Holders = 1 };
                    created = true;
                }
            }
            if (removing is not null)
            {
                await removing;
                continue;
            }
            if (created)
                _ = Connect(registration);
            return registration;
        }
    }

    /// <summary>Puts the server and waits for its status to turn connected. Bounded by the connect timeout; never throws.</summary>
    private async Task Connect(Registration registration)
    {
        string? failure;
        using var timeout = new CancellationTokenSource(timings.Connect);
        var registered = false;
        try
        {
            await oc.PutMcpServerAsync(registration.Name, registration.Directory, registration.Config, timeout.Token);
            registered = true;
            failure = await Poll(registration, timeout.Token);
            if (failure is null)
                await Task.Delay(timings.Settle, CancellationToken.None);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            var seconds = timings.Connect.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
            failure = registered ? $"{registration.Last}: no connection within {seconds} s" : $"rejected: no answer within {seconds} s";
        }
        catch (Exception e)
        {
            failure = $"rejected: {Reason(e)}";
        }
        registration.Connected.SetResult(failure is null ? null : Cut(Redact(failure, registration.Config)));
    }

    private async Task<string?> Poll(Registration registration, CancellationToken ct)
    {
        while (true)
        {
            var listed = (await oc.McpServersAsync(registration.Directory, ct)).FirstOrDefault(s => s.Name == registration.Name);
            registration.Last = listed?.Status ?? "absent";
            if (listed?.Status == "connected")
                return null;
            if (listed is { Status: not "pending" })
                return listed.Error is { Length: > 0 } error ? $"{listed.Status}: {error}" : listed.Status;
            await Task.Delay(timings.Poll, ct);
        }
    }

    /// <summary>Drops one hold; the last holder removes the server, after any connect still under way has finished. The
    /// server can stay if the <c>DELETE</c> fails (until OpenCode restarts), which the run's span notes.</summary>
    private async Task Release(Registration registration, CancellationToken ct)
    {
        TaskCompletionSource closing;
        lock (_gate)
        {
            if (--registration.Holders > 0)
                return;
            registration.Closing = closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            await registration.Connected.Task;
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(RemoveTimeout);
            await oc.RemoveMcpServerAsync(registration.Name, registration.Directory, bound.Token);
        }
        catch (OpenCodeException e) when (e.Status == HttpStatusCode.NotFound)
        {
        }
        catch (Exception e)
        {
            Activity.Current?.SetTag($"chargehand.service.{registration.Server}.remove_failed", Cut(Redact(Reason(e), registration.Config)));
        }
        finally
        {
            lock (_gate)
                _registrations.Remove((registration.Directory, registration.Name));
            closing.SetResult();
        }
    }

    private static McpConfigBody ConfigOf(ServiceTransport transport) => transport switch
    {
        HttpServiceTransport http => new("remote", Url: http.Url.AbsoluteUri, Headers: http.Headers.Count > 0 ? http.Headers : null),
        StdioServiceTransport stdio => new("local", Command: stdio.Command, Environment: stdio.Env.Count > 0 ? stdio.Env : null),
        _ => throw new NotSupportedException($"{transport.GetType().Name} is not a transport OpenCode takes"),
    };

    /// <summary>Eight hex digits of a keyed hash of the config, header and environment values included.</summary>
    private string Tag(McpConfigBody config)
    {
        var canonical = JsonSerializer.Serialize(new object?[] { config.Type, config.Url, config.Command, Sorted(config.Headers), Sorted(config.Environment) });
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(canonical)))[..8];
    }

    private static string[][]? Sorted(IReadOnlyDictionary<string, string>? values) =>
        values?.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => new[] { v.Key, v.Value }).ToArray();

    /// <summary>A tool's part of the permission action: OpenCode writes the characters outside <c>[A-Za-z0-9_-]</c> as <c>_</c>
    /// (spike, ADR 0034), which also keeps a wildcard out of a rule.</summary>
    private static string ActionPart(string tool) => string.Concat(tool.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_'));

    private static string Reason(Exception e) => e is ChargehandException ? e.Message : string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : e.Message;

    /// <summary>The text with the config's credentials taken out: what the server says about a failure may repeat what it was sent.</summary>
    private static string Redact(string text, McpConfigBody config)
    {
        var values = (config.Headers?.Values ?? []).Concat(config.Environment?.Values ?? []).ToList();
        var secrets = values.Where(v => v.Length >= 4).Concat(values.SelectMany(v => v.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(v => v.Length >= 6))
            .Distinct().OrderByDescending(v => v.Length);
        foreach (var secret in secrets)
            text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return ChargehandException.Scrub(text);
    }

    private static string Cut(string text)
    {
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > MaxReason ? text[..MaxReason] + "…" : text;
    }
}

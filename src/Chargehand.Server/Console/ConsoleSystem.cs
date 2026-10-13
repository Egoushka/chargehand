using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Containers;
using Chargehand.Driven;

namespace Chargehand.Server;

/// <summary>A secret item is shown as set or not, never by name or value.</summary>
public sealed record ProfileHttp(int Port, string Listen, IReadOnlyList<string> AllowedHosts, int MaxConcurrentRuns, bool Ui, bool ApiKeySet, IReadOnlyList<string> Accounts);

public sealed record ProfileClaudeCode(string Version, string Binary, string Credential, bool Gateway);

public sealed record ProfileOpenCode(string Version, string Host);

/// <summary>A runner service the profile lists (ADR 0058): its name and address as scheme, host and port, never its key.</summary>
public sealed record ProfileRunner(string Name, string? Host, int? MaxSessions, string Platform, double Weight);

public sealed record ProfileIntegrator(bool AppConfigured, IReadOnlyDictionary<string, string> Repositories, IReadOnlyList<string> AlwaysAsk, DrivenCiFix CiFix);

public sealed record ProfileDriven(bool Enabled, int MaxParallel, int MaxParallelTotal, int MaxSessionCalls, IReadOnlyList<string> Images, IReadOnlyList<string> NetworkAllow, IReadOnlyList<ProfileRunner> Runners, bool PushSecretSet,
    DrivenLimits? Limits, DrivenGovernor? Governor, DrivenAdmission? Admission, ProfileIntegrator? Integrator, DrivenPlanning? Planning, DrivenLearning? Learning, string? ForgeKind, string? ForgeHost,
    IReadOnlyList<string> ServiceRepositories, bool TaskSourceSet);

public sealed record ProfileAutomation(IReadOnlyList<string> Repositories, int AllowedAuthors, string Label, string Mention, int PollSeconds, int MaxConcurrent, int MaxStartsPerDay, int MaxReviewRounds);

/// <summary>The profile as the owner may see it: settings only. Secret items, URLs' paths and queries, headers, environment values, commands and key files are left out.</summary>
public sealed record ProfileView(string Schema, string Runtime, string DefaultPreset, string? IntakeModel, string WorkerRoot, IReadOnlyList<string> RepositoryRoots, decimal RunCapUsd, int MaxParallelNodes, bool SupportCheck,
    bool Signing, bool Telemetry, IReadOnlyList<string> Memory, IReadOnlyList<string> McpServers, IReadOnlyDictionary<string, string> Models, ProfileHttp? Http, ProfileClaudeCode? ClaudeCode,
    ProfileOpenCode? OpenCode, ProfileDriven? Driven, ProfileAutomation? Automation);

public sealed record PresetBody(string Name, string? Version, IReadOnlyList<string> AllowedActions, bool Driven, bool Approval, string? Error);

/// <param name="Kind"><c>session</c>, <c>egress</c>, <c>service</c>, <c>workspace</c> (prep and transfer helpers) or <c>other</c>.</param>
public sealed record ContainerBody(string Name, string Kind, string? RunId, string? BatchRunId, string? TaskId, string? Actor);

/// <param name="Reachable">The runner answered <c>GET /count</c> (or the local engine answered). Null when driven sessions are off.</param>
public sealed record RunnerBody(bool Configured, string? Host, bool? Reachable, string? Error, long? ElapsedMs, int? ContainerCount, IReadOnlyList<ContainerBody> Containers, IReadOnlyList<string> Networks,
    IReadOnlyList<string> Volumes, HostBody? HostSnapshot);

/// <summary>The host that runs the session containers, as admission reads it (ADR 0045). <c>FreeAfterReserveMb</c> is available memory less <c>driven.admission.reserve_mb</c>.</summary>
public sealed record HostBody(long? AvailableMb, long? TotalMb, long? SwapTotalMb, long? SwapFreeMb, double? Load1, int? Cpus, long? FreeAfterReserveMb, int ReserveMb, double? MaxLoadPerCpu);

/// <summary>One runner the profile lists (ADR 0058), from the pool's last probe: an answer younger than five seconds is reused, and a runner that did not answer is not asked again until its back-off ends.</summary>
/// <param name="Reachable">The runner answered <c>GET /host</c>; null when driven sessions are off.</param>
/// <param name="Draining">The runner said it takes no new session; null when it did not answer or does not report a state.</param>
/// <param name="Running">Sessions of this server admitted there; null with one runner, which does not count them.</param>
public sealed record RunnerStatusBody(string Name, string? Host, bool? Reachable, string? Error, long? ElapsedMs, bool? Draining, string? DrainingReason, int? Running, int? MaxSessions, int? ContainerCount);

public sealed record ImageBody(string Reference, string Name, string? Digest, bool Pinned);

/// <summary><c>system/v1</c>: version, the redacted profile, presets, the first runner and its live containers, every runner's state, and the pinned images. Owner only.</summary>
public sealed record SystemBody(string ContractVersion, DateTimeOffset GeneratedAt, string Version, DateTimeOffset StartedAt, bool Halted, int Unfinished, ProfileView? Profile, IReadOnlyList<PresetBody> Presets,
    RunnerBody Runner, IReadOnlyList<RunnerStatusBody> Runners, IReadOnlyList<ImageBody> Images, IReadOnlyList<string> NotRecorded);

internal static class ConsoleSystem
{
    public const string Contract = "system/v1";

    private static readonly TimeSpan Probe = TimeSpan.FromSeconds(3);

    private static readonly IReadOnlyList<string> NotRecorded =
    [
        "The runner lists container, network and volume names only: their start time, state and memory use are not served.",
        "A container is tied to a batch and task by the saved state of unfinished batches; one the server no longer holds state for shows without them.",
        "Presets list what the file says; whether a change to one passed Prompt CI is not recorded here.",
    ];

    public static string Version { get; } = ComputeVersion();

    private static string ComputeVersion()
    {
        var assembly = typeof(Orchestrator).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return info is { Length: > 0 } ? info.Split('+')[0] : assembly.GetName().Version?.ToString(3) ?? "0";
    }

    public static async Task<SystemBody> BuildAsync(ConsoleContext c, CancellationToken ct)
    {
        var (runner, snapshot) = await RunnerAsync(c, ct);
        var runners = await RunnersAsync(c, runner, snapshot, ct);
        var images = (c.Profile?.Driven?.Images ?? []).Select(Image).ToList();
        return new SystemBody(Contract, c.Now, Version, Process.GetCurrentProcess().StartTime.ToUniversalTime(), c.Runs.Halted, c.Runs.Held.Count(r => !r.IsChild), c.Profile is null ? null : View(c.Profile),
            Presets(c), runner, runners, images, NotRecorded);
    }

    internal static ImageBody Image(string reference)
    {
        var at = reference.IndexOf('@');
        var digest = at >= 0 ? reference[(at + 1)..] : null;
        return new ImageBody(reference, at >= 0 ? reference[..at] : reference, digest, digest is not null && digest.StartsWith("sha256:", StringComparison.Ordinal) && digest.Length == "sha256:".Length + 64);
    }

    private static List<PresetBody> Presets(ConsoleContext c)
    {
        List<PresetBody> presets = [];
        foreach (var name in c.Runs.Presets())
        {
            try
            {
                var preset = Preset.Load(c.Settings.PresetsDirectory, name);
                presets.Add(new PresetBody(name, preset.Version, preset.AllowedActions, preset.Driven is not null, preset.Approval is not null, null));
            }
            catch (Exception e) when (e is InvalidDataException or ChargehandException or IOException or YamlDotNet.Core.YamlException)
            {
                presets.Add(new PresetBody(name, null, [], false, false, e.Message));
            }
        }
        return presets;
    }

    private static async Task<(RunnerBody Runner, HostSnapshot? Snapshot)> RunnerAsync(ConsoleContext c, CancellationToken ct)
    {
        var driven = c.Driven;
        var runner = c.Profile?.Driven?.ResolvedRunners() is { Count: > 0 } runners ? runners[0] : null;
        var host = runner is null ? null : HostOf(runner.Url);
        if (driven?.Engine is not { } engine)
            return (new RunnerBody(runner is not null, host, null, null, null, null, [], [], [], null), null);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Probe);
        var clock = Stopwatch.StartNew();
        int count;
        EngineResources resources;
        try
        {
            count = await engine.CountAsync(limit.Token);
            resources = await engine.ListAsync(limit.Token);
        }
        catch (Exception e) when (e is ChargehandException or OperationCanceledException or HttpRequestException or IOException)
        {
            var failed = await ReadHostAsync(c, ct);
            return (new RunnerBody(runner is not null, host, false, e is OperationCanceledException ? $"no answer within {Probe.TotalSeconds:0} s" : e.Message, clock.ElapsedMilliseconds, null, [], [], [],
                HostOf(c, failed)), failed);
        }
        var live = driven.LiveBatches();
        var snapshot = await ReadHostAsync(c, ct);
        return (new RunnerBody(runner is not null, host, true, null, clock.ElapsedMilliseconds, count, [.. resources.Containers.Order(StringComparer.Ordinal).Select(n => Container(n, live))],
            [.. resources.Networks.Order(StringComparer.Ordinal)], [.. resources.Volumes.Order(StringComparer.Ordinal)], HostOf(c, snapshot)), snapshot);
    }

    /// <summary>Every runner the profile lists. With several the pool's probe state is read, and a runner that answered is asked for its container count; with one, <paramref name="first"/> is reused.</summary>
    private static async Task<IReadOnlyList<RunnerStatusBody>> RunnersAsync(ConsoleContext c, RunnerBody first, HostSnapshot? snapshot, CancellationToken ct)
    {
        var configured = c.Profile?.Driven?.ResolvedRunners() ?? [];
        if (c.Driven?.Pool is { } pool)
        {
            var statuses = await pool.StatusAsync(ct);
            return await Task.WhenAll(statuses.Select(async s => new RunnerStatusBody(s.Site.Name, HostOf(s.Site.Config.Url), s.Reachable, s.Why, s.ElapsedMs, s.Reachable ? s.Snapshot?.State is null ? null : s.Snapshot.IsDraining : null,
                s.Reachable && s.Snapshot?.IsDraining == true ? s.Snapshot.StateReason : null, s.Running, s.Site.Config.MaxSessions, s.Reachable ? await CountAsync(s.Site.Engine, ct) : null)));
        }
        return [.. configured.Select((r, i) => i == 0 && c.Driven?.Engine is not null
            ? new RunnerStatusBody(r.Name, HostOf(r.Url), first.Reachable, first.Error, first.ElapsedMs, snapshot?.State is null ? null : snapshot.IsDraining, snapshot?.IsDraining == true ? snapshot.StateReason : null, null,
                r.MaxSessions, first.ContainerCount)
            : new RunnerStatusBody(r.Name, HostOf(r.Url), null, null, null, null, null, null, r.MaxSessions, null))];
    }

    private static async Task<int?> CountAsync(IContainerEngine engine, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Probe);
        try
        {
            return await engine.CountAsync(limit.Token);
        }
        catch (Exception e) when (e is ChargehandException or OperationCanceledException or HttpRequestException or IOException)
        {
            return null;
        }
    }

    internal static async Task<HostBody?> HostAsync(ConsoleContext c, CancellationToken ct) => HostOf(c, await ReadHostAsync(c, ct));

    private static HostBody? HostOf(ConsoleContext c, HostSnapshot? s)
    {
        var admission = c.Profile?.Driven?.Admission ?? new DrivenAdmission();
        return s is null ? null : new HostBody(s.AvailableMb, s.TotalMb, s.SwapTotalMb, s.SwapFreeMb, s.Load1, s.Cpus, s.AvailableMb - admission.ReserveMb, admission.ReserveMb, admission.MaxLoadPerCpu);
    }

    private static async Task<HostSnapshot?> ReadHostAsync(ConsoleContext c, CancellationToken ct)
    {
        if (c.Driven?.Host is not { } source)
            return null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Probe);
        try
        {
            return await source.ReadAsync(limit.Token);
        }
        catch (Exception e) when (e is ChargehandException or OperationCanceledException or HttpRequestException or IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>A container's name tells its kind and the run it belongs to (<c>chargehand-&lt;run&gt;</c> for a session, <c>chargehand-egress-&lt;batch&gt;</c>, <c>chargehand-svc-&lt;service&gt;-&lt;batch&gt;</c>,
    /// <c>chargehand-prep-</c> and <c>chargehand-xfer-&lt;run&gt;</c>); the saved state of an unfinished batch ties a session's name to its batch and task.</summary>
    internal static ContainerBody Container(string name, IReadOnlyList<BatchState> live)
    {
        string? After(string prefix) => name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : null;
        if (After("chargehand-egress-") is { } egress)
            return new ContainerBody(name, "egress", null, egress, null, live.FirstOrDefault(b => b.RunId == egress)?.Actor);
        if (After("chargehand-svc-") is { } svc)
        {
            var batch = svc.IndexOf('-') is > 0 and var dash ? svc[(dash + 1)..] : null;
            return new ContainerBody(name, "service", null, batch, null, live.FirstOrDefault(b => b.RunId == batch)?.Actor);
        }
        if ((After("chargehand-prep-") ?? After("chargehand-xfer-")) is { } helper)
            return new ContainerBody(name, "workspace", helper, live.FirstOrDefault(b => b.RunIds.Contains(helper))?.RunId, null, live.FirstOrDefault(b => b.RunIds.Contains(helper))?.Actor);
        if (After("chargehand-") is { } run)
        {
            var owner = live.FirstOrDefault(b => b.Tasks.Any(t => t.RunId == run || t.Runs?.Contains(run) == true));
            return new ContainerBody(name, owner is null ? "other" : "session", run, owner?.RunId, owner?.Tasks.FirstOrDefault(t => t.RunId == run || t.Runs?.Contains(run) == true)?.Id, owner?.Actor);
        }
        return new ContainerBody(name, "other", null, null, null, null);
    }

    private static string? HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}" : null;

    internal static ProfileView View(Profile p)
    {
        var http = p.Http;
        var claude = p.ClaudeCode;
        var driven = p.Driven;
        var kind = p.Runtime ?? (claude is not null ? "claude_code" : p.Opencode is not null ? "opencode" : "auto");
        return new ProfileView(p.Schema, kind, p.DefaultPreset, p.IntakeModel, p.WorkerRoot, p.Roots, p.RunCapUsd, p.MaxParallelNodes, p.SupportCheck, p.Signing?.KeyFile is not null, p.Telemetry is not null,
            [.. (p.Memory ?? []).Select(m => m.Name)], [.. (p.McpServers ?? new Dictionary<string, McpServerSettings>()).Keys.Order(StringComparer.Ordinal)], p.Models ?? new Dictionary<string, string>(),
            http is null ? null : new ProfileHttp(http.Port, http.Listen, http.AllowedHosts ?? [], http.MaxConcurrentRuns, http.Ui, !string.IsNullOrEmpty(http.ApiKeySecret), [.. (http.Accounts ?? []).Select(a => a.Name)]),
            claude is null ? null : new ProfileClaudeCode(claude.Version, claude.Binary, claude.ApiKeySecret is not null ? "api_key" : claude.OauthTokenSecret is not null ? "oauth_token" : "login", claude.BaseUrl is not null),
            p.Opencode is null ? null : new ProfileOpenCode(p.Opencode.Version, HostOf(p.Opencode.Url) ?? ""),
            driven is null ? null : new ProfileDriven(driven.Enabled, driven.MaxParallel, driven.MaxParallelTotal, driven.MaxSessionCalls, driven.Images ?? [], driven.Network?.Allow ?? [],
                [.. driven.ResolvedRunners().Select(r => new ProfileRunner(r.Name, HostOf(r.Url), r.MaxSessions, r.Platform, r.Weight))], driven.PushSecret is not null, driven.Limits, driven.Governor, driven.Admission,
                driven.Integrator is null ? null : new ProfileIntegrator(driven.Integrator.AppId is not null, driven.Integrator.Repositories ?? new Dictionary<string, string>(), driven.Integrator.AlwaysAsk ?? [], driven.Integrator.CiFix ?? new DrivenCiFix()),
                driven.Planning, driven.Learning, driven.Forge?.Kind, driven.Forge?.BaseUrl is { } forge ? HostOf(forge) : null,
                [.. (driven.Services?.Repositories ?? new Dictionary<string, IReadOnlyList<DrivenTestService>>()).Keys.Order(StringComparer.Ordinal)], driven.TaskSource is not null),
            p.Automation?.Github is { } github
                ? new ProfileAutomation([.. (github.Repositories ?? new Dictionary<string, AutomationRepository>()).Keys.Order(StringComparer.Ordinal)], github.AllowedAuthors?.Count ?? 0, github.Label, github.Mention, github.PollSeconds,
                    github.MaxConcurrent, github.MaxStartsPerDay, github.MaxReviewRounds)
                : null);
    }
}

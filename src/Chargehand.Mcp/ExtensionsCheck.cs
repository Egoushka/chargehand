using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Config;
using Chargehand.Memory;
using ModelContextProtocol.Protocol;

namespace Chargehand.Mcp;

/// <param name="Lines">One line per item checked: every <c>mcp_servers</c> entry, then each memory provider's mapped tools, then each
/// preset's services. A line for a problem ends with <c>; action: </c> and what to do about it.</param>
/// <param name="Ok">False when any line reports a problem.</param>
public sealed record ExtensionsCheckResult(IReadOnlyList<string> Lines, bool Ok);

/// <summary>
/// <c>chargehand extensions check</c> (ADR 0034, spec decision 13): connects every server of the profile, lists its tools, and
/// checks that each tool a memory mapping or a preset's <c>services</c> names is there, so a setup mistake shows before a run
/// does. A problem is a line and a flag, never an exception: whatever a server throws is a finding, and only the caller's own
/// cancellation ends the check early. The lines name servers, tools and argument names only; a reason has URLs and
/// credentials scrubbed, and no argument value or recalled fact is printed. A <c>prompt_enhancer</c> (ADR 0040) is checked for the two tools it calls.
/// </summary>
public static partial class ExtensionsCheck
{
    private const string ActionMark = "; action: ";
    private const int MaxReasonLength = 200;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <param name="presets">The presets whose <c>services</c> are checked; see <see cref="LoadPresets"/>.</param>
    /// <param name="probeQuery">When set, each memory provider whose recall mapping checks out also runs one real recall with it,
    /// and the line says how many facts came back.</param>
    /// <param name="timeout">How long connecting to and listing one server may take, and one probe (default 30 s).</param>
    public static async Task<ExtensionsCheckResult> RunAsync(
        Profile profile, IReadOnlyList<Preset> presets, McpConnectionPool pool, string? probeQuery, CancellationToken ct, TimeSpan? timeout = null)
    {
        var report = new Report();
        var servers = profile.McpServers ?? new Dictionary<string, McpServerSettings>();
        var names = servers.Keys.Order(StringComparer.Ordinal).ToList();
        var listings = (await Task.WhenAll(names.Select(name => ListAsync(pool, name, timeout ?? DefaultTimeout, ct)))).Zip(names).ToDictionary(p => p.Second, p => p.First);
        foreach (var name in names)
            if (listings[name].Tools is { } tools)
                report.Pass($"server {name}: connected, {Count(tools.Count, "tool")}");
            else
                report.Fail($"server {name}: {listings[name].Reason}", listings[name].Action!);

        var providers = profile.Memory ?? [];
        foreach (var problem in MemoryMapping.Validate(providers, servers))
            report.Fail(problem, "fix memory in the profile; docs/guide/reference.md lists the fields");
        foreach (var provider in providers.Where(p => MemoryMapping.Validate(p, servers).Count == 0))
            await CheckMemoryAsync(provider, listings[provider.Server], probeQuery, timeout ?? DefaultTimeout, pool, report, ct);

        if (profile.PromptEnhancer is { } enhancer)
            CheckEnhancer(enhancer, listings, report);

        foreach (var preset in presets.OrderBy(p => p.Name, StringComparer.Ordinal))
            foreach (var use in preset.NodeKinds.Values.SelectMany(k => k.Services ?? []).GroupBy(u => u.Server))
                CheckService(preset.Name, use.Key, [.. use.SelectMany(u => u.Tools).Distinct()], servers, listings, report);

        if (report.Lines.Count == 0)
            report.Pass("nothing to check: the profile lists no mcp_servers, memory or preset services");
        return new ExtensionsCheckResult(report.Lines, report.Ok);
    }

    /// <summary>
    /// The presets to check: every <c>*.yaml</c> in <paramref name="directory"/>, or just <paramref name="only"/>. A preset that does
    /// not load is a problem line in the second list, not an exception.
    /// </summary>
    /// <exception cref="ChargehandException"><paramref name="only"/> names a preset that does not exist.</exception>
    public static (IReadOnlyList<Preset> Presets, IReadOnlyList<string> Problems) LoadPresets(string directory, string? only)
    {
        var presets = new List<Preset>();
        var problems = new List<string>();
        var names = only is not null ? [only] : (Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.yaml") : [])
            .Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal).ToList();
        foreach (var name in names)
        {
            try
            {
                presets.Add(Preset.Load(directory, name));
            }
            catch (ChargehandException) when (only is not null)
            {
                throw;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                problems.Add($"preset {name}: {Reason(e)}{ActionMark}fix presets/{name}.yaml");
            }
        }
        return (presets, problems);
    }

    private static async Task CheckMemoryAsync(
        MemoryProviderSettings provider, Listing listing, string? probeQuery, TimeSpan timeout, McpConnectionPool pool, Report report, CancellationToken ct)
    {
        var who = $"memory {provider.Name}";
        if (listing.Tools is not { } tools)
        {
            report.Fail($"{who}: server '{provider.Server}' is not available, so its tools were not checked", $"fix server '{provider.Server}' first; its line is above");
            return;
        }
        var recallOk = false;
        foreach (var (operation, call) in new[] { ("recall", provider.Tools.Recall), ("retain", provider.Tools.Retain), ("invalidate", provider.Tools.Invalidate) })
        {
            if (call is null)
                continue;
            var at = $"memory.{provider.Name}.tools.{operation}";
            var found = tools.FirstOrDefault(t => t.Name == call.Tool);
            var problems = found is null
                ? [($"tool '{call.Tool}' is not listed by server '{provider.Server}'", $"add the tool to the server, or fix {at}.tool in the profile")]
                : ArgumentProblems(call, found).Select(p => (p, $"fix {at}.arguments in the profile to match the tool's schema")).ToList();
            foreach (var (problem, action) in problems)
                report.Fail($"{who}: {operation} -> {problem}", action);
            if (problems.Count == 0)
                report.Pass($"{who}: {operation} -> {call.Tool} ok");
            recallOk |= operation == "recall" && problems.Count == 0;
        }
        if (probeQuery is not null && recallOk)
            await ProbeAsync(provider, probeQuery, timeout, pool, report, ct);
    }

    /// <summary>What the tool's input schema says against the mapping's top-level argument names: a name it does not declare (when it
    /// declares any and does not allow others), and a required one the mapping leaves out.</summary>
    private static IEnumerable<string> ArgumentProblems(ToolCall call, Tool tool)
    {
        var schema = tool.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object)
            yield break;
        var declared = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject().Select(p => p.Name).ToList()
            : [];
        var open = schema.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind != JsonValueKind.False;
        if (declared.Count > 0 && !open)
            foreach (var name in call.Arguments.Keys.Where(a => !declared.Contains(a)))
                yield return $"argument '{name}' is not a property of {call.Tool} (properties: {string.Join(", ", declared)})";
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            foreach (var name in required.EnumerateArray().Select(r => r.GetString()).Where(r => r is not null && !call.Arguments.ContainsKey(r)))
                yield return $"required argument '{name}' of {call.Tool} is not in the mapping";
    }

    private static async Task ProbeAsync(MemoryProviderSettings provider, string query, TimeSpan timeout, McpConnectionPool pool, Report report, CancellationToken ct)
    {
        try
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(timeout);
            var facts = await new McpMemoryProvider(provider, pool).RecallAsync(query, new MemoryScope(provider.Name, provider.EffectiveNamespace), bound.Token);
            report.Pass($"memory {provider.Name}: probe returned {facts.Count} fact(s)");
        }
        catch (Exception e)
        {
            ct.ThrowIfCancellationRequested();
            report.Fail($"memory {provider.Name}: probe failed: {(e is OperationCanceledException ? $"no answer within {Seconds(timeout)} s" : Reason(e))}",
                $"check memory.{provider.Name}.tools.recall (arguments and results) against what the server answers");
        }
    }

    private static void CheckEnhancer(PromptEnhancerSettings enhancer, IReadOnlyDictionary<string, Listing> listings, Report report)
    {
        var who = "prompt_enhancer";
        if (listings[enhancer.Server].Tools is not { } tools)
        {
            report.Fail($"{who}: server '{enhancer.Server}' is not available, so its tools were not checked", $"fix server '{enhancer.Server}' first; its line is above");
            return;
        }
        foreach (var name in McpPromptEnhancer.RequiredTools)
            if (tools.Any(t => t.Name == name))
                report.Pass($"{who}: {name} ok");
            else
                report.Fail($"{who}: tool '{name}' is not listed by server '{enhancer.Server}'", $"point prompt_enhancer.server at a server that lists enhance and feedback");
    }

    private static void CheckService(
        string preset, string server, IReadOnlyList<string> patterns, IReadOnlyDictionary<string, McpServerSettings> servers,
        IReadOnlyDictionary<string, Listing> listings, Report report)
    {
        var who = $"preset {preset}: service {server}";
        if (!servers.ContainsKey(server))
        {
            report.Fail($"{who}: server '{server}' is not defined in mcp_servers (defined: {(servers.Count == 0 ? "none" : string.Join(", ", servers.Keys.Order(StringComparer.Ordinal)))})",
                "add it to mcp_servers in the profile, or remove it from the preset's services");
            return;
        }
        if (listings[server].Tools is not { } tools)
        {
            report.Fail($"{who}: server is not available, so its tools were not checked", $"fix server '{server}' first; its line is above");
            return;
        }
        foreach (var pattern in patterns)
        {
            var glob = ServiceResolver.Glob(pattern);
            var hits = tools.Count(t => glob.IsMatch(t.Name));
            if (hits == 0)
                report.Fail($"{who}: tool '{pattern}' is not listed", $"remove it from the preset's services, or add the tool to server '{server}'");
            else
                report.Pass($"{who}: {pattern} ok{(pattern.Contains('*', StringComparison.Ordinal) ? $" ({Count(hits, "tool")})" : "")}");
        }
    }

    private static async Task<Listing> ListAsync(McpConnectionPool pool, string name, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(timeout);
            var client = await pool.GetAsync(name, bound.Token);
            var listed = await client.ListToolsAsync(cancellationToken: bound.Token);
            return new Listing([.. listed.Select(t => t.ProtocolTool)], null, null);
        }
        catch (McpUnavailableException e)
        {
            return new Listing(null, $"{e.Code}: {Reason(e.Detail)}", e.Code == McpUnavailableException.SecretUnresolved
                ? "make the secret resolvable through the profile's secrets sources (an environment variable, or a command), then run the check again"
                : $"check that mcp_servers.{name} points at a running server, then run the check again");
        }
        catch (Exception e)
        {
            ct.ThrowIfCancellationRequested();
            var reason = e is OperationCanceledException ? $"no answer within {Seconds(timeout)} s" : Reason(e);
            return new Listing(null, $"{McpUnavailableException.Unreachable}: {reason}", $"check that mcp_servers.{name} points at a running server, then run the check again");
        }
    }

    private static string Reason(Exception e) => Reason(string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : e.Message);

    /// <summary>One line, keys and bearer tokens scrubbed, every URL (it may carry user information or a token) replaced, cut.</summary>
    private static string Reason(string message)
    {
        var text = string.Join(' ', Url().Replace(ChargehandException.Scrub(message), "<url>").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= MaxReasonLength ? text : text[..MaxReasonLength];
    }

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]*://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    /// <param name="Tools">The server's tools; null when it could not be reached.</param>
    private sealed record Listing(IReadOnlyList<Tool>? Tools, string? Reason, string? Action);

    private sealed class Report
    {
        public List<string> Lines { get; } = [];

        public bool Ok { get; private set; } = true;

        public void Pass(string line) => Lines.Add(line);

        public void Fail(string line, string action)
        {
            Ok = false;
            Lines.Add(line + ActionMark + action);
        }
    }
}

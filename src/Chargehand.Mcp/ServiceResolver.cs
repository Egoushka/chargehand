using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Config;
using Chargehand.RunLog;
using Chargehand.Runtime;

namespace Chargehand.Mcp;

/// <summary>
/// Resolves a preset's <c>services</c> against the profile's <c>mcp_servers</c> (ADR 0034): the server's URL or command with
/// its secrets filled in, then a connection through the pool to list its tools, then the preset's names and globs expanded
/// against that list. A service whose server, secret or connection fails is dropped and reported; so is a requested tool the
/// server does not list, and the service keeps the rest. Only the caller's own cancellation is thrown.
/// </summary>
/// <param name="listTimeout">How long connecting and listing the tools of one server may take (default 30 s), so a server that
/// accepts a connection and never answers cannot hold a run.</param>
public sealed class ServiceResolver(McpConnectionPool pool, TimeSpan? listTimeout = null) : IServiceResolver
{
    private readonly TimeSpan _listTimeout = listTimeout ?? TimeSpan.FromSeconds(30);

    public async Task<ResolvedServices> ResolveAsync(IReadOnlyList<ServiceUse> uses, CancellationToken ct)
    {
        var grants = new List<ServiceGrant>();
        var report = new List<ServiceReport>();
        // A server the preset lists twice is one grant with the tools of both entries.
        foreach (var server in uses.GroupBy(u => u.Server))
        {
            var (grant, issues) = await ResolveOne(server.Key, [.. server.SelectMany(u => u.Tools).Distinct()], ct);
            if (grant is not null)
                grants.Add(grant);
            report.Add(new ServiceReport(server.Key, grant?.Tools ?? [], issues));
        }
        return new ResolvedServices(grants, report);
    }

    private async Task<(ServiceGrant? Grant, IReadOnlyList<string> Issues)> ResolveOne(string server, IReadOnlyList<string> patterns, CancellationToken ct)
    {
        try
        {
            // Secrets first, unbounded by the listing timeout: a command source has its own limit.
            var transport = await Task.Run(() => pool.Transport(server), ct);
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(_listTimeout);
            var client = await pool.GetAsync(server, bound.Token);
            var listed = (await client.ListToolsAsync(cancellationToken: bound.Token)).Select(t => t.ProtocolTool).ToList();

            var granted = new SortedSet<string>(StringComparer.Ordinal);
            var issues = new List<string>();
            foreach (var pattern in patterns)
            {
                var glob = Glob(pattern);
                var hits = listed.Select(t => t.Name).Where(name => glob.IsMatch(name)).ToList();
                if (hits.Count == 0)
                    issues.Add($"tool_missing: {pattern}");
                granted.UnionWith(hits);
            }
            if (granted.Count == 0)
                return (null, issues);
            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                listed.Where(t => granted.Contains(t.Name)).OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => new object?[] { t.Name, t.Description, t.InputSchema })))));
            var hidden = listed.Select(t => t.Name).Where(name => !granted.Contains(name)).Order(StringComparer.Ordinal).ToList();
            return (new ServiceGrant(server, transport, [.. granted], digest, hidden), issues);
        }
        catch (McpUnavailableException e)
        {
            return (null, [$"{e.Code}: {(e.Code == McpUnavailableException.UnknownServer ? e.Server : e.Detail)}"]);
        }
        catch (Exception e)
        {
            // Whatever the server or its client threw, it is a service failure; only the caller's own cancellation is not.
            ct.ThrowIfCancellationRequested();
            var reason = e is OperationCanceledException
                ? $"no answer within {_listTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s"
                : string.Join(' ', ChargehandException.Scrub(string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : e.Message).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return (null, [$"{McpUnavailableException.Unreachable}: {reason}"]);
        }
    }

    /// <summary>A tool name pattern: <c>*</c> is any run of characters, everything else is literal.</summary>
    private static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
}

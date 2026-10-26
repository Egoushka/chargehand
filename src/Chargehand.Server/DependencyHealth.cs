using Chargehand.Modules.Forge.Api;
using Chargehand.Onboarding;

namespace Chargehand.Server;

/// <summary>One thing the profile configures that a task needs (CHARGEHAND-316). <paramref name="CheckAsync"/> reads, never writes, and returns null when it works or a plain reason when it does not: never a secret value.</summary>
public sealed record DependencyProbe(string Name, Func<CancellationToken, Task<string?>> CheckAsync);

/// <summary>What <c>GET /v1/health?deep=1</c> says of one <see cref="DependencyProbe"/>.</summary>
public sealed record DependencyCheck(string Name, bool Ok, string? Reason);

public static class DependencyHealth
{
    /// <summary>Each probe's own bound, so one dead host does not hold the canary for the intake model's 60 s.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>All probes at once, each bounded by <paramref name="timeout"/>; the answer keeps the probes' order.</summary>
    public static async Task<IReadOnlyList<DependencyCheck>> RunAsync(IReadOnlyList<DependencyProbe> probes, TimeSpan timeout, CancellationToken ct) =>
        await Task.WhenAll(probes.Select(p => RunOneAsync(p, timeout, ct)));

    private static async Task<DependencyCheck> RunOneAsync(DependencyProbe probe, TimeSpan timeout, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout);
        try
        {
            var reason = await probe.CheckAsync(bounded.Token);
            return new DependencyCheck(probe.Name, reason is null, reason);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DependencyCheck(probe.Name, false, $"no answer within {timeout.TotalSeconds:0} s");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new DependencyCheck(probe.Name, false, e.Message);
        }
    }

    /// <summary>A runner: the probe <c>chargehand doctor</c> makes (<c>ProbeRunnerAsync</c>), so the two cannot disagree on what a healthy runner is.</summary>
    public static DependencyProbe Runner(string name, Func<CancellationToken, Task<ProbeOutcome>> probe) =>
        new(name, async ct => ((await probe(ct)) switch
        {
            { Status: { } status } => Judge("the runner", (System.Net.HttpStatusCode)status),
            { Failure: var failure } => $"the runner does not answer: {failure}",
        }));

    /// <summary>The GitHub App: listing its installations needs a valid App key and signs nothing into a repository.</summary>
    public static DependencyProbe GitHubApp(IInstallationTokens tokens) =>
        new("github app", async ct => (await tokens.AccountsAsync(ct)).Count == 0 ? "the App is installed on no account" : null);

    /// <summary>The push token: GitHub names the user it belongs to when it is valid.</summary>
    public static DependencyProbe PushToken(Uri githubApi, Func<string> token, HttpMessageHandler? handler = null) =>
        Get("push token", githubApi, "user", () => new("Bearer", token()), "GitHub", handler);

    private static DependencyProbe Get(string name, Uri baseUrl, string path, Func<System.Net.Http.Headers.AuthenticationHeaderValue> auth, string what, HttpMessageHandler? handler) =>
        new(name, async ct =>
        {
            using var client = Client(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, path.TrimStart('/')));
            request.Headers.Authorization = auth();
            request.Headers.UserAgent.ParseAdd("chargehand");
            return Judge(what, (await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)).StatusCode);
        });

    private static HttpClient Client(HttpMessageHandler? handler) => handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);

    private static string? Judge(string what, System.Net.HttpStatusCode status) => (int)status switch
    {
        >= 200 and < 300 => null,
        401 or 403 => $"{what} refuses the key (HTTP {(int)status})",
        var other => $"{what} answered HTTP {other}",
    };
}

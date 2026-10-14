using System.Net.Http.Headers;
using Chargehand.Config;
using Chargehand.Modules.Gateway.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.Runtime;

namespace Chargehand.Onboarding;

/// <summary><see cref="IDoctorEnvironment"/> on this machine. A test passes a handler to keep the probes off the network.</summary>
public sealed class SystemDoctorEnvironment(HttpMessageHandler? handler = null) : IDoctorEnvironment
{
    /// <summary>The most directories under one root that are scanned.</summary>
    private const int MaxChildren = 200;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public Profile LoadProfile(string path) => Profile.Load(path);

    public bool SecretResolves(Profile profile, string item)
    {
        try
        {
            _ = profile.Secret(item);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public string? EnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public bool OnPath(string binary) => RuntimeSelector.OnPath(binary);

    public IReadOnlyList<FoundRepository> Repositories(string root)
    {
        if (IsRepository(root))
            return [new(Path.GetFileName(Path.TrimEndingDirectorySeparator(root)), RepositoryDetector.Scan(root))];
        var children = Directory.EnumerateDirectories(root).Where(IsRepository).Order(StringComparer.Ordinal).Take(MaxChildren)
            .Select(d => new FoundRepository(Path.GetFileName(d), RepositoryDetector.Scan(d))).ToList();
        if (children.Count > 0)
            return children;
        var self = RepositoryDetector.Scan(root);
        return self.Ecosystems.Count > 0 ? [new(Path.GetFileName(Path.TrimEndingDirectorySeparator(root)), self)] : [];
    }

    public Task<ProbeOutcome> ProbeServerAsync(Profile profile, HttpSettings http, CancellationToken ct) =>
        ProbeAsync(Doctor.ServerUrl(http), profile, http.ApiKeySecret, "/v1/runs?limit=1", ct);

    public Task<ProbeOutcome> ProbeRunnerAsync(Profile profile, DrivenRunner runner, CancellationToken ct) =>
        Uri.TryCreate(runner.Url, UriKind.Absolute, out var url)
            ? ProbeAsync(url, profile, runner.ApiKeySecret, "/count", ct)
            : Task.FromResult(new ProbeOutcome(null, "driven.runner.url is not an absolute URL"));

    private static bool IsRepository(string directory) => Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git"));

    private async Task<ProbeOutcome> ProbeAsync(Uri baseUrl, Profile profile, string keyItem, string path, CancellationToken ct)
    {
        string key;
        try
        {
            key = profile.Secret(keyItem);
        }
        catch (InvalidOperationException)
        {
            return new(null, $"the key item {keyItem} does not resolve");
        }
        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = ProbeTimeout;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var response = await client.GetAsync(new Uri(baseUrl, path), HttpCompletionOption.ResponseHeadersRead, ct);
            return new((int)response.StatusCode);
        }
        catch (HttpRequestException e)
        {
            return new(null, e.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(null, $"no answer within {ProbeTimeout.TotalSeconds:0} s");
        }
    }
}

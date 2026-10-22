using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Chargehand.Config;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Gateway.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Onboarding;

/// <summary>What <c>chargehand adopt</c> runs on the machine: git and the container engine. Null exit code: stopped at <paramref name="limit"/>.</summary>
public interface IAdoptHost
{
    Task<(int? ExitCode, string Output)> RunAsync(string program, IReadOnlyList<string> args, string? workingDirectory, TimeSpan limit, CancellationToken ct);
}

/// <summary>
/// <c>chargehand adopt &lt;owner/repo&gt;</c>: finds the repository's checkout under the profile's repository roots, starts the session image on a copy of it and runs the
/// setup, build, test and lint commands the detector proposes there, then says what to set in the profile (<c>driven.services.verify</c>, written with <c>--write</c>), runs one
/// read-only canary run on the server with <c>--canary</c>, and lists what is missing: the toolchain, the container engine, a CI workflow. Nothing is pushed. Exit codes: 0 a test
/// command passed in the image, 1 none did or a step could not run, 2 usage error.
/// </summary>
public static class AdoptCli
{
    public const string Usage = "usage: chargehand adopt <owner/repo> [--path <checkout>] [--image <ref>] [--write] [--canary] [--canary-preset <name>] [--url <server>] [--timeout <seconds>]";

    private const string Copy = "/tmp/adopt";

    private sealed record Options(string Repository, string? Path, string? Image, bool Write, bool Canary, string CanaryPreset, string? Url, TimeSpan Limit);

    /// <param name="connect">Opens a client of the server for the canary, as <c>watch</c> does; given the <c>--url</c> arguments.</param>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, string profilePath, IAdoptHost host, Func<IReadOnlyList<string>, (HttpClient? Client, string? Problem)> connect,
        TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (Parse(args) is not { } o)
        {
            error.WriteLine(Usage);
            return 2;
        }
        var profile = Profile.Load(profilePath);
        List<string> missing = [];
        output.WriteLine($"adopt {o.Repository}");
        var path = o.Path is { } given ? Path.GetFullPath(given) : await FindAsync(profile, o.Repository, host, ct);
        if (path is null || !Directory.Exists(path))
        {
            output.WriteLine($"fail checkout: none under repository_roots has origin {o.Repository}");
            output.WriteLine($"  clone it into a root: git clone https://github.com/{o.Repository}.git {Path.Combine((profile.Roots.Count > 0 ? profile.Roots[0] : "."), o.Repository.Split('/')[^1])}");
            return 1;
        }
        var rev = await host.RunAsync("git", ["rev-parse", "HEAD"], path, TimeSpan.FromSeconds(10), ct);
        var head = rev.ExitCode == 0 ? rev.Output.Trim() : "";
        output.WriteLine($"ok   checkout: {path}{(head.Length >= 7 ? $" (HEAD {head[..7]})" : "")}");

        var scan = RepositoryDetector.Scan(path);
        foreach (var e in scan.Ecosystems)
            output.WriteLine($"ok   ecosystem: {e.Name} ({string.Join(", ", e.Markers)})");
        if (scan.Ecosystems.Count == 0)
            output.WriteLine("warn ecosystem: none detected from marker files");
        if (!RepositoryReadiness.CiPresent(path))
        {
            output.WriteLine("fail ci: no CI configuration (.github/workflows/*.yml or another forge's file)");
            missing.Add("a CI workflow that builds and tests every pull request");
        }
        else
            output.WriteLine("ok   ci: configured");

        var results = await ProbeAsync(o, profile, scan, path, host, missing, output, ct);
        var verify = AdoptProbes.Verify(results);
        ProposeVerify(o, profilePath, verify, output, missing);
        if (o.Canary)
            await CanaryAsync(o, connect, path, head, output, missing, ct);
        else
            output.WriteLine("skip canary: pass --canary to start one read-only run on the server");
        output.WriteLine(missing.Count == 0 ? "missing: nothing" : $"missing: {string.Join("; ", missing.Distinct(StringComparer.Ordinal))}");
        return verify is null ? 1 : 0;
    }

    private static Options? Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0].StartsWith("--", StringComparison.Ordinal) || args[0].Split('/') is not { Length: 2 } parts || parts.Any(string.IsNullOrWhiteSpace))
            return null;
        var o = new Options(args[0], null, null, false, false, "cheap", null, TimeSpan.FromMinutes(10));
        for (var i = 1; i < args.Count; i++)
        {
            var value = i + 1 < args.Count ? args[i + 1] : null;
            Options? next = (args[i], value) switch
            {
                ("--write", _) => o with { Write = true },
                ("--canary", _) => o with { Canary = true },
                ("--path", { } v) => o with { Path = v },
                ("--image", { } v) => o with { Image = v },
                ("--canary-preset", { } v) => o with { CanaryPreset = v },
                ("--url", { } v) => o with { Url = v },
                ("--timeout", { } v) when int.TryParse(v, out var seconds) && seconds > 0 => o with { Limit = TimeSpan.FromSeconds(seconds) },
                _ => null,
            };
            if (next is null)
                return null;
            if (args[i] is not ("--write" or "--canary"))
                i++;
            o = next;
        }
        return o;
    }

    /// <summary>The checkout under a repository root whose <c>origin</c> is the repository; null when none is.</summary>
    private static async Task<string?> FindAsync(Profile profile, string repository, IAdoptHost host, CancellationToken ct)
    {
        foreach (var root in profile.Roots.Where(Directory.Exists))
            foreach (var dir in new[] { root }.Concat(Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal)))
            {
                if (!Directory.Exists(Path.Combine(dir, ".git")) && !File.Exists(Path.Combine(dir, ".git")))
                    continue;
                var url = (await host.RunAsync("git", ["config", "--get", "remote.origin.url"], dir, TimeSpan.FromSeconds(5), ct)).Output.Trim();
                if (RemoteRepository.Parse(url, profile.Driven?.Forge) is { } remote && string.Equals($"{remote.Owner}/{remote.Name}", repository, StringComparison.OrdinalIgnoreCase))
                    return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
            }
        return null;
    }

    private static async Task<IReadOnlyList<AdoptResult>> ProbeAsync(Options o, Profile profile, RepositoryScan scan, string path, IAdoptHost host, List<string> missing, TextWriter output,
        CancellationToken ct)
    {
        var image = o.Image ?? (profile.Driven?.Images is { Count: > 0 } images ? SharedServicesPlan.PickImage(profile.Driven.Services, scan.Ecosystems.Select(e => e.Name), images[0]) : null);
        if (image is null)
        {
            output.WriteLine("fail image: driven.images is empty; build the session image and pin it, or pass --image");
            missing.Add("a session image (driven.images)");
            return [];
        }
        var engine = await host.RunAsync("docker", ["version", "--format", "{{.Server.Version}}"], null, TimeSpan.FromSeconds(20), ct);
        if (engine.ExitCode != 0)
        {
            output.WriteLine("fail docker: the container engine does not answer (docker version)");
            missing.Add("Docker: start the engine, or run adopt where the runner is");
            return [];
        }
        output.WriteLine($"ok   docker: {engine.Output.Trim()}");
        var probes = AdoptProbes.For(scan, path);
        if (probes.Count == 0)
        {
            output.WriteLine("fail probes: no build or test command proposed; add a script such as scripts/check.sh");
            missing.Add("one command that builds and tests the project");
            return [];
        }
        var name = $"chargehand-adopt-{Guid.NewGuid():N}"[..28];
        var started = await host.RunAsync("docker", ["run", "-d", "--rm", "--name", name, "-v", $"{path}:/src:ro", "--entrypoint", "sleep", image, "3600"], null, TimeSpan.FromMinutes(5), ct);
        if (started.ExitCode != 0)
        {
            output.WriteLine($"fail image: {image} did not start: {Last(started.Output)}");
            missing.Add($"a session image that starts ({image})");
            return [];
        }
        output.WriteLine($"ok   image: {image}");
        try
        {
            await host.RunAsync("docker", ["exec", name, "sh", "-c", $"mkdir -p {Copy} && cp -a /src/. {Copy}"], null, TimeSpan.FromMinutes(2), ct);
            return await RunProbesAsync(probes, name, o.Limit, host, missing, output, ct);
        }
        finally
        {
            await host.RunAsync("docker", ["rm", "-f", name], null, TimeSpan.FromSeconds(30), CancellationToken.None);
        }
    }

    private static async Task<IReadOnlyList<AdoptResult>> RunProbesAsync(IReadOnlyList<AdoptProbe> probes, string container, TimeSpan limit, IAdoptHost host, List<string> missing,
        TextWriter output, CancellationToken ct)
    {
        List<AdoptResult> results = [];
        foreach (var probe in probes)
        {
            if (probe.Kind == "test" && results.Any(r => r.Probe.Kind == "test" && r.Passed))
                break;
            var clock = Stopwatch.StartNew();
            var (exit, text) = await host.RunAsync("docker", ["exec", "-w", Copy, container, .. probe.Command], null, limit, ct);
            var result = AdoptProbes.Classify(probe, exit, text, clock.Elapsed);
            results.Add(result);
            output.WriteLine(result.Outcome switch
            {
                AdoptProbes.Passed => $"ok   {probe.Kind}: {probe.Line} ({(int)result.Took.TotalSeconds} s)",
                AdoptProbes.MissingTool => $"fail {probe.Kind}: {probe.Line}: {result.Tool} is not in the session image",
                AdoptProbes.TimedOut => $"fail {probe.Kind}: {probe.Line}: still running after {(int)limit.TotalSeconds} s",
                _ => $"fail {probe.Kind}: {probe.Line}: exit {exit}\n{Indent(result.Tail)}",
            });
            if (result.Outcome == AdoptProbes.MissingTool)
                missing.Add($"toolchain: {result.Tool} in the session image");
        }
        if (!results.Any(r => r.Probe.Kind == "test" && r.Passed))
            missing.Add("a test command that passes in the session image");
        return results;
    }

    private static void ProposeVerify(Options o, string profilePath, AdoptProbe? verify, TextWriter output, List<string> missing)
    {
        if (verify is null)
        {
            output.WriteLine("skip profile: no test command passed, so none is proposed for driven.services.verify");
            return;
        }
        if (!o.Write)
        {
            output.WriteLine($"profile: add {AdoptProbes.Snippet(o.Repository, verify.Command)} (or pass --write)");
            return;
        }
        var text = File.Exists(profilePath) ? File.ReadAllText(profilePath) : "{ \"contract_version\": \"profile/v1\" }";
        if (AdoptProbes.WithVerify(text, o.Repository, verify.Command) is not { } updated)
        {
            output.WriteLine($"fail profile: {profilePath} is not plain JSON, so it is left as it is; add {AdoptProbes.Snippet(o.Repository, verify.Command)}");
            missing.Add("the driven.services.verify entry in the profile");
            return;
        }
        File.WriteAllText(profilePath, updated);
        output.WriteLine($"ok   profile: driven.services.verify[\"{o.Repository}\"] = {verify.Line}, written to {profilePath}");
    }

    /// <summary>One read-only run on the server against the checkout, followed to its result as <c>watch</c> follows a run.</summary>
    private static async Task CanaryAsync(Options o, Func<IReadOnlyList<string>, (HttpClient? Client, string? Problem)> connect, string path, string head, TextWriter output,
        List<string> missing, CancellationToken ct)
    {
        var (client, problem) = connect(o.Url is null ? [] : ["--url", o.Url]);
        if (client is null)
        {
            output.WriteLine($"fail canary: {problem}");
            missing.Add("a reachable server for the canary");
            return;
        }
        using (client)
        {
            var request = new JsonObject
            {
                ["contract_version"] = "request/v1",
                ["text"] = "Canary from chargehand adopt: name the commands that build, test and lint this repository and the file that defines each. Change nothing.",
                ["context"] = new JsonObject
                {
                    ["interactive"] = false,
                    ["preset"] = o.CanaryPreset,
                    ["repository"] = new JsonObject { ["path"] = path, ["commit"] = head },
                },
            };
            using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/runs") { Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json") };
            message.Headers.Add("Prefer", "wait=0");
            using var reply = await client.SendAsync(message, ct);
            if (!reply.IsSuccessStatusCode || reply.Headers.Location?.OriginalString.Split('/')[^1] is not { Length: > 0 } runId)
            {
                output.WriteLine($"fail canary: the server answered {(int)reply.StatusCode}: {Last(await reply.Content.ReadAsStringAsync(ct))}");
                missing.Add("a canary run the server accepts");
                return;
            }
            output.WriteLine($"canary: {runId}");
            var exit = await Watch.WatchCli.WatchAsync(client, runId, output, output, ct);
            output.WriteLine(exit == 0 ? "ok   canary: completed" : $"fail canary: ended with exit {exit}");
            if (exit != 0)
                missing.Add("a canary run that completes");
        }
    }

    private static string Last(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    private static string Indent(string text) => string.Join('\n', text.Split('\n').Select(l => $"       {l}"));
}

/// <summary>The machine's git and container engine for <see cref="AdoptCli"/>.</summary>
public sealed class SystemAdoptHost : IAdoptHost
{
    public async Task<(int? ExitCode, string Output)> RunAsync(string program, IReadOnlyList<string> args, string? workingDirectory, TimeSpan limit, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = workingDirectory ?? "" };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (127, $"{program}: not found ({e.Message})");
        }
        if (process is null)
            return (127, $"{program}: not found");
        using (process)
        {
            process.StandardInput.Close();
            var text = new StringBuilder();
            var lockText = new object();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (lockText) text.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (lockText) text.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(limit);
            try
            {
                await process.WaitForExitAsync(timer.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return (null, text.ToString());
            }
            process.WaitForExit();
            lock (lockText)
                return (process.ExitCode, text.ToString());
        }
    }
}

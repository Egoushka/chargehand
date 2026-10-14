using System.Globalization;
using Chargehand.Config;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Bench;

/// <summary><c>chargehand bench run &lt;taskset&gt;</c> and <c>chargehand bench report &lt;results.json&gt; [&lt;other.json&gt;]</c> (ADR 0052).</summary>
public static class BenchCli
{
    public const string Usage = """
        usage: chargehand [--profile <path>] bench run <taskset.json> [--dry-run] [--baseline] [--check-pins] [--only <id,id>]
                          [--remote <url template>] [--max-tokens <n>] [--max-usd <n>] [--max-parallel <n>]
                          [--workdir <dir>] [--out <dir>] [--accept-root <dir>] [--url <server>]
               chargehand bench report <results.json> [<other.json>] [--out <file>]
          run --dry-run   checks the task set, the pins and that every acceptance command exists; starts nothing, calls no model
          run --baseline  runs the repositories' test commands and every acceptance check on the pinned commits in containers: each check must
                          behave as the set says (a change's check fails, an impossible task's guard passes); calls no model
          run             one driven batch per repository on the server (--url, CHARGEHAND_URL; key CHARGEHAND_API_KEY), then the acceptance checks;
                          needs --remote (CHARGEHAND_BENCH_REMOTE) and a cap per batch; writes results.json and report.md to --out
          report          renders a results file again, or compares two (the first is the base)
        """;

    private static readonly string[] Flags = ["--dry-run", "--baseline", "--check-pins"];
    private static readonly string[] Valued = ["--only", "--remote", "--max-tokens", "--max-usd", "--max-parallel", "--workdir", "--out", "--accept-root", "--url"];

    public static int Report(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        if (!Parse(args, ["--out"], [], out var positional, out var options, out var problem) || positional.Count is < 1 or > 2)
        {
            error.WriteLine(problem ?? Usage);
            return 2;
        }
        List<BenchResults> loaded = [];
        foreach (var path in positional)
        {
            try
            {
                loaded.Add(BenchResults.Parse(File.ReadAllText(path)) is { Schema: BenchResults.SchemaId } parsed ? parsed : throw new FormatException($"not a {BenchResults.SchemaId} file"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
            {
                error.WriteLine($"{path}: {e.Message}");
                return 2;
            }
        }
        var text = loaded.Count == 1 ? BenchReport.Render(loaded[0]) : BenchReport.Compare(loaded[0], loaded[1], Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(positional[0]))) ?? "A", Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(positional[1]))) ?? "B");
        if (options.TryGetValue("--out", out var outFile))
            File.WriteAllText(outFile, text);
        else
            output.Write(text);
        return 0;
    }

    /// <param name="Engines">The container engine, workspace helper and output-volume helper the profile selects (the runner service, or the local Docker CLI); not called for a dry run.</param>
    /// <param name="Server">The HTTP client of the server with its key set; not called for a dry run or a baseline.</param>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, Profile profile, IReadOnlyList<string> presetAllow, AcceptanceLimits limits,
        Func<(IContainerEngine Engine, IWorkspaceEngine Workspace, IOutVolumeEngine Files)> engines, Func<IReadOnlyList<string>, (HttpClient? Client, string? Problem)> server,
        Func<string, string?> env, string workingDirectory, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (!Parse(args, Valued, Flags, out var positional, out var options, out var problem) || positional.Count != 1)
        {
            error.WriteLine(problem ?? Usage);
            return 2;
        }
        var set = TaskSetLoader.Load(positional[0], out var problems);
        if (set is null || problems.Count > 0)
        {
            foreach (var p in problems)
                error.WriteLine(p);
            return 2;
        }
        long? maxTokens = null;
        decimal? maxUsd = null;
        int? maxParallel = null;
        if ((options.TryGetValue("--max-tokens", out var t) && !TryNumber(t, out maxTokens)) || (options.TryGetValue("--max-usd", out var u) && !TryNumber(u, out maxUsd))
            || (options.TryGetValue("--max-parallel", out var par) && (!TryNumber(par, out maxParallel) || maxParallel is < 1 or > 4)))
        {
            error.WriteLine("--max-tokens and --max-usd need a positive number; --max-parallel 1 to 4.");
            return 2;
        }
        IReadOnlySet<string>? only = options.TryGetValue("--only", out var ids) ? ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal) : null;
        var dryRun = options.ContainsKey("--dry-run");
        var baseline = options.ContainsKey("--baseline");
        if (dryRun && baseline)
        {
            error.WriteLine("--dry-run and --baseline are two different checks; pick one.");
            return 2;
        }

        var driven = profile.Driven;
        var allow = presetAllow.Concat(driven?.Network?.Allow ?? []).Distinct().ToList();
        string? ImageFor(BenchRepository repo) => driven?.Images is { Count: > 0 } images ? SharedServicesPlan.PickImage(driven.Services, [repo.Ecosystem], images[0]) : null;
        var mode = driven?.Governor?.Mode is { } m ? m.ToString().ToLowerInvariant() : null;
        var environment = new BenchEnvironment(ImageFor, allow, mode, TimeSpan.FromMinutes(15));
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var workDirectory = Path.GetFullPath(options.GetValueOrDefault("--workdir") ?? Path.Combine(workingDirectory, "runs", "bench", "work"));
        var outDirectory = Path.GetFullPath(options.GetValueOrDefault("--out") ?? Path.Combine(workingDirectory, "runs", "bench", $"{set.Name}-{stamp}"));
        var bench = new BenchOptions(outDirectory, environment, only, dryRun, baseline, options.ContainsKey("--check-pins"), options.GetValueOrDefault("--remote") ?? env("CHARGEHAND_BENCH_REMOTE"),
            maxTokens, maxUsd, maxParallel);
        var workspace = new GitBenchWorkspace(workDirectory, options.GetValueOrDefault("--accept-root"));

        if (dryRun)
            return await new BenchRunner(workspace, null, null, output).RunAsync(set, bench, ct);
        if (baseline)
        {
            var (engine, helper, files) = engines();
            return await new BenchRunner(workspace, null, new ContainerAcceptanceRunner(engine, helper, files, limits), output).RunAsync(set, bench, ct);
        }

        // The server comes first: a run that cannot reach one should not have started a container engine.
        var (client, serverProblem) = server(options.TryGetValue("--url", out var url) ? ["--url", url] : []);
        if (client is null)
        {
            error.WriteLine(serverProblem);
            return 2;
        }
        using (client)
        {
            var (engine, helper, files) = engines();
            return await new BenchRunner(workspace, new BenchServer(client), new ContainerAcceptanceRunner(engine, helper, files, limits), output).RunAsync(set, bench, ct);
        }
    }

    private static bool TryNumber<T>(string text, out T? value) where T : struct, IParsable<T>, IComparable<T>
    {
        value = null;
        if (!T.TryParse(text, CultureInfo.InvariantCulture, out var parsed) || parsed.CompareTo(default) <= 0)
            return false;
        value = parsed;
        return true;
    }

    /// <summary>Flags and valued options in any order after the positional arguments.</summary>
    private static bool Parse(IReadOnlyList<string> args, IReadOnlyCollection<string> valued, IReadOnlyCollection<string> flags, out List<string> positional, out Dictionary<string, string> options, out string? problem)
    {
        positional = [];
        options = [];
        problem = null;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (flags.Contains(a))
                options[a] = "";
            else if (valued.Contains(a))
            {
                if (i + 1 >= args.Count)
                {
                    problem = $"{a} needs a value.\n{Usage}";
                    return false;
                }
                options[a] = args[++i];
            }
            else if (a.StartsWith("--", StringComparison.Ordinal))
            {
                problem = $"unknown option {a}.\n{Usage}";
                return false;
            }
            else
                positional.Add(a);
        }
        return true;
    }
}

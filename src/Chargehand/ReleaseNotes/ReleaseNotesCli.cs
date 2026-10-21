using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Verification;

namespace Chargehand.ReleaseNotes;

/// <summary><c>chargehand release-notes &lt;repository path&gt; --from &lt;tag or commit&gt; [--to &lt;ref&gt;]</c>: reads the first-parent log of a range
/// with local git and prints a <c>request/v1</c> for the <c>release-notes</c> preset on stdout, to pipe into <c>chargehand run</c> or post to the
/// HTTP interface. Workers have no shell and no history, so the log reaches them as inputs: one per commit, its id the first 12 hex characters of
/// the hash (CHARGEHAND-191). It needs no profile and makes no network call. Exit codes: 0 printed, 1 the range is unusable, 2 usage error.</summary>
public static partial class ReleaseNotesCli
{
    public const string Usage = "usage: chargehand release-notes <repository path> --from <tag or commit> [--to <ref, default HEAD>]";

    /// <summary>Commits listed to the worker; a longer range lists the newest and says how many it left out.</summary>
    public const int MaxCommits = 200;

    private const int IdLength = 12;
    private const int MaxTitleLength = 300;

    public static async Task<int> RunAsync(IReadOnlyList<string> args, string workingDirectory, TextWriter output, TextWriter error, CancellationToken ct)
    {
        string? repository = null, from = null, to = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--from" when i + 1 < args.Count:
                    from = args[++i];
                    break;
                case "--to" when i + 1 < args.Count:
                    to = args[++i];
                    break;
                case var positional when !positional.StartsWith('-') && repository is null:
                    repository = positional;
                    break;
                default:
                    error.WriteLine(Usage);
                    return 2;
            }
        }
        if (repository is null || from is null)
        {
            error.WriteLine(Usage);
            return 2;
        }
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository, workingDirectory));
        if (!Directory.Exists(path))
        {
            error.WriteLine($"{path} is not a directory");
            return 2;
        }
        var (request, problem) = await BuildAsync(path, from, to ?? "HEAD", ct);
        if (request is null)
        {
            error.WriteLine(problem);
            return 1;
        }
        output.WriteLine(JsonSerializer.Serialize(request, ContractJson.Options));
        return 0;
    }

    /// <summary>The request for the commits reachable from <paramref name="to"/> and not from <paramref name="from"/>, first parents only, or the reason there is none.</summary>
    public static async Task<(RunRequest? Request, string? Problem)> BuildAsync(string repository, string from, string to, CancellationToken ct)
    {
        if (from.StartsWith('-') || to.StartsWith('-'))
            return (null, "a ref cannot start with '-'");
        var previous = await Git(repository, ct, "rev-parse", "--verify", "--quiet", $"{from}^{{commit}}");
        if (previous is null)
            return (null, $"{from} is not a commit or tag of {repository}");
        var target = await Git(repository, ct, "rev-parse", "--verify", "--quiet", $"{to}^{{commit}}");
        if (target is null)
            return (null, $"{to} is not a commit or tag of {repository}");
        previous = previous.Trim();
        target = target.Trim();
        if (await Git(repository, ct, "merge-base", "--is-ancestor", previous, target) is null)
            return (null, $"{from} is not an ancestor of {to}: the notes cover what {to} added since {from}");

        var range = $"{previous}..{target}";
        var total = int.Parse((await Git(repository, ct, "rev-list", "--first-parent", "--count", range) ?? "0").Trim(), CultureInfo.InvariantCulture);
        if (total == 0)
            return (null, $"no commits between {from} and {to}");
        var log = await Git(repository, ct, "log", "--first-parent", $"--max-count={MaxCommits}", "--format=%H%x1f%s%x1f%b%x1e", range) ?? "";
        var commits = Parse(log);

        var left = total - commits.Count;
        var summary = $"{from}..{to} ({previous[..IdLength]}..{target[..IdLength]}): {commits.Count} of {total} first-parent commits are listed, newest first" +
            (left > 0 ? $"; the oldest {left} are not listed, so the notes are incomplete" : "");
        var inputs = new List<CallerInput> { new("range", "range", summary) };
        inputs.AddRange(commits.Select(c => new CallerInput(c.Sha[..IdLength], "commit", c.Title)));
        return (new RunRequest("request/v1", $"Write release notes for {from}..{to}.", new RequestContext(false, "release-notes", Repository: new RepositoryRef(repository, target)), inputs), null);
    }

    internal static IReadOnlyList<(string Sha, string Title)> Parse(string log) =>
        [.. log.Split('\u001e', StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim('\n', '\r', ' ')).Where(e => e.Length > 0).Select(e =>
        {
            var fields = e.Split('\u001f');
            return (Sha: fields[0], Title: Title(fields[1], fields.Length > 2 ? fields[2] : ""));
        })];

    /// <summary>The subject, except for a merge commit's, which names a branch: then the first line of its body, the pull request's title.</summary>
    private static string Title(string subject, string body)
    {
        var title = subject;
        if (MergeSubject().IsMatch(subject) && body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) is { } first)
            title = first;
        title = Whitespace().Replace(title, " ").Trim();
        return title.Length <= MaxTitleLength ? title : title[..(MaxTitleLength - 1)] + "…";
    }

    private static Task<string?> Git(string repository, CancellationToken ct, params string[] args) => GitEvidenceResolver.Git(repository, ct, args);

    [GeneratedRegex(@"^Merge (pull request|branch|remote-tracking branch)\b")]
    private static partial Regex MergeSubject();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

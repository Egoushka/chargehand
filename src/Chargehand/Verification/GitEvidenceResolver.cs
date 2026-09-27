using System.Diagnostics;
using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Runtime;

namespace Chargehand.Verification;

/// <summary>Resolves references with local git and the node's own records. No network.</summary>
public sealed partial class GitEvidenceResolver : IEvidenceResolver
{
    public async Task<IReadOnlyList<EvidenceFailure>> ResolveAsync(ResultContract contract, EvidenceScope scope, CancellationToken ct)
    {
        var failures = new List<EvidenceFailure>();
        foreach (var e in contract.Evidence)
        {
            var reason = e.Kind switch
            {
                EvidenceKind.File => await CheckFile(e, scope, ct),
                EvidenceKind.Commit => await Git(scope.RepositoryPath, ct, "cat-file", "-e", $"{e.Locator}^{{commit}}") is null ? $"commit {e.Locator} not found" : null,
                EvidenceKind.SessionMessage => scope.MessageIds.Contains(e.Locator) ? null : $"message {e.Locator} not in the node's session",
                EvidenceKind.Input => scope.InputIds.Contains(e.Locator) ? null : $"input {e.Locator} was not supplied by the caller (input ids: {string.Join(", ", scope.InputIds)})",
                EvidenceKind.Url => scope.SeenText.Contains(e.Locator, StringComparison.Ordinal) ? null : "URL not seen in the node's inputs or tool output",
                EvidenceKind.Diff => CheckDiff(e.Locator, scope.Diff),
                _ => $"unknown kind {e.Kind}",
            };
            if (reason is not null)
                failures.Add(new EvidenceFailure(e.Id, reason));
        }
        return failures;
    }

    private static async Task<string?> CheckFile(Evidence e, EvidenceScope scope, CancellationToken ct)
    {
        if (e.Commit is { Length: > 0 } c && !scope.Commit.StartsWith(c, StringComparison.OrdinalIgnoreCase))
            return $"cites commit {c}, node ran at {scope.Commit[..Math.Min(12, scope.Commit.Length)]}";
        if (!TryParseRange(e.Locator, out var path, out var start, out var end))
            return "locator must be path:line or path:start-end";
        if (path.StartsWith('/') || path.Split('/').Contains(".."))
            return "path must be relative to the repository root";
        var text = await Git(scope.RepositoryPath, ct, "show", $"{scope.Commit}:{path}");
        if (text is null)
            return $"{path} does not exist at the node's commit";
        var lines = text.Length == 0 ? 0 : text.TrimEnd('\n').Split('\n').Length;
        return end > lines ? $"line {end} beyond end of file ({lines} lines)" : null;
    }

    private static string? CheckDiff(string locator, IReadOnlyList<FileDiff> diff)
    {
        var hasRange = TryParseRange(locator, out var path, out var start, out var end);
        var file = diff.FirstOrDefault(d => d.File == (hasRange ? path : locator));
        if (file is null)
            return $"{(hasRange ? path : locator)} is not in the node's session diff";
        if (!hasRange)
            return null;
        foreach (Match h in Hunk().Matches(file.Patch))
        {
            var from = int.Parse(h.Groups["s"].Value);
            var len = h.Groups["l"].Success ? int.Parse(h.Groups["l"].Value) : 1;
            if (start <= from + len - 1 && end >= from)
                return null;
        }
        return $"lines {start}-{end} are outside the changed hunks of {path}";
    }

    internal static bool TryParseRange(string locator, out string path, out int start, out int end)
    {
        var m = Locator().Match(locator);
        path = m.Success ? m.Groups["p"].Value : locator;
        start = m.Success ? int.Parse(m.Groups["a"].Value) : 0;
        end = m.Success && m.Groups["b"].Success ? int.Parse(m.Groups["b"].Value) : start;
        return m.Success && start >= 1 && end >= start;
    }

    /// <summary>stdout, or null when git exits non-zero.</summary>
    private static async Task<string?> Git(string repo, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        _ = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return p.ExitCode == 0 ? await stdout : null;
    }

    [GeneratedRegex(@"^(?<p>[^:]+):(?<a>\d+)(?:-(?<b>\d+))?$")]
    private static partial Regex Locator();

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(?<s>\d+)(?:,(?<l>\d+))? @@", RegexOptions.Multiline)]
    private static partial Regex Hunk();
}

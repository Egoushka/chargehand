using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Chargehand.Workspace;

/// <param name="Directory">The checkout the worker reads: the base checkout itself when nothing was applied.</param>
/// <param name="Commit">The commit at <paramref name="Directory"/>: a commit of the clone, not of the caller's repository, when something was applied.</param>
/// <param name="Applied">Files of the diff now in the tree.</param>
/// <param name="Skipped">Files of the diff that were neither applied nor already there: a cut or conflicting patch, a symlink.</param>
public sealed record ReviewedChange(string Directory, string Commit, IReadOnlyList<string> Applied, IReadOnlyList<string> Skipped)
{
    /// <summary>What the worker is told when part of the diff is in its tree; null when none is.</summary>
    public string? Note => Applied.Count == 0
        ? null
        : $"The checkout holds the change of the diff input: {Applied.Count} of {Applied.Count + Skipped.Count} files of the diff input are in it, so read and cite their lines in the checkout."
          + (Skipped.Count == 0 ? "" : $" Not in it, review these from the diff input: {string.Join(", ", Skipped)}.");

    /// <summary>
    /// A review reads the change it reviews. The caller pins the commit it changed from and sends the change as a diff, since the changed commit may exist only
    /// where the caller works. This clones the base checkout, applies the diff file by file (a diff cut at a size limit still gives its whole files) and commits it
    /// in the clone, which is named for the diff and reused. A file the base commit already holds in its changed form is left alone, so a diff of a commit that is
    /// itself pinned changes nothing. The base checkout is never written (ADR 0015).
    /// </summary>
    /// <returns>The base checkout and commit, unchanged, when no file of the diff applies.</returns>
    public static async Task<ReviewedChange> ApplyAsync(string checkout, string commit, string diff, CancellationToken ct)
    {
        var files = Split(diff);
        if (files.Count == 0)
            return new ReviewedChange(checkout, commit, [], []);
        var dir = $"{checkout}.change-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(diff)))[..12]}";
        if (System.IO.Directory.Exists(Path.Combine(dir, ".git")) && await Git(dir, null, ct, "rev-parse", "--verify", "HEAD") is { } kept)
            return Result(dir, kept, files, SkippedIn(await Git(dir, null, ct, "log", "-1", "--format=%b") ?? ""));
        var temp = $"{dir}.tmp-{Guid.NewGuid():N}";
        try
        {
            var parent = Path.GetDirectoryName(dir)!;
            if ((await Git(parent, null, ct, "clone", "-q", "--local", "--no-checkout", "--", checkout, temp) is null
                 && await Git(parent, null, ct, "clone", "-q", "--no-hardlinks", "--no-checkout", "--", checkout, temp) is null)
                || await Git(temp, null, ct, "checkout", "-q", "--detach", commit) is null)
                return new ReviewedChange(checkout, commit, [], files.Select(f => f.Path).ToList());
            var changed = 0;
            var skipped = new List<string>();
            foreach (var file in files)
            {
                // A header with no hunk (a caller's stub) has no content to read: applying it would leave an empty file that looks like the change.
                if (file.Patch.Contains("\n@@ ", StringComparison.Ordinal) && await Git(temp, file.Patch, ct, "apply", "--check", "--reverse", "-") is not null)
                    continue;
                if (file.Patch.Contains("\n@@ ", StringComparison.Ordinal) && !file.Symlink && await Git(temp, file.Patch, ct, "apply", "--whitespace=nowarn", "-") is not null)
                    changed++;
                else
                    skipped.Add(file.Path);
            }
            if (changed == 0)
                return Result(checkout, commit, files, skipped);
            await Git(temp, null, ct, "add", "-A");
            // Hooks stay off and signing is not attempted: the repository's own hooks are not ours to run. The message records what was skipped, for a later reuse.
            var message = "the diff input applied\n\n" + string.Concat(skipped.Select(s => $"skipped: {s}\n"));
            if (await Git(temp, null, ct, "-c", "core.hooksPath=/dev/null", "-c", "commit.gpgsign=false", "-c", "user.name=chargehand", "-c", "user.email=chargehand@localhost",
                    "commit", "-q", "-m", message) is null
                || await Git(temp, null, ct, "rev-parse", "HEAD") is not { } head)
                return Result(checkout, commit, files, files.Select(f => f.Path).ToList());
            try
            {
                System.IO.Directory.Move(temp, dir);
            }
            catch (IOException)
            {
                // Another run made the same clone first.
                if (await Git(dir, null, ct, "rev-parse", "--verify", "HEAD") is not { } other)
                    throw;
                head = other;
            }
            return Result(dir, head, files, skipped);
        }
        finally
        {
            if (System.IO.Directory.Exists(temp))
                System.IO.Directory.Delete(temp, recursive: true);
        }
    }

    private static ReviewedChange Result(string directory, string commit, List<FilePatch> files, IReadOnlyList<string> skipped) =>
        new(directory, commit, files.Select(f => f.Path).Where(p => !skipped.Contains(p)).ToList(), skipped);

    private static List<string> SkippedIn(string message) =>
        [.. message.Split('\n').Where(l => l.StartsWith("skipped: ", StringComparison.Ordinal)).Select(l => l["skipped: ".Length..])];

    private sealed record FilePatch(string Path, string Patch, bool Symlink);

    /// <summary>One patch per <c>diff --git</c> section; a diff without those headers is one patch.</summary>
    private static List<FilePatch> Split(string diff)
    {
        var sections = Regex.Split(diff, @"(?m)^(?=diff --git )").Where(s => s.StartsWith("diff --git ", StringComparison.Ordinal)).ToList();
        if (sections.Count == 0)
        {
            var plain = Regex.Match(diff, @"(?m)^\+\+\+ (?:b/)?(\S+)");
            return plain.Success && diff.Contains("\n@@ ", StringComparison.Ordinal) ? [new FilePatch(plain.Groups[1].Value, diff.TrimEnd() + "\n", false)] : [];
        }
        return sections.Select(s =>
        {
            var header = Regex.Match(s, @"^diff --git a/(?<a>.+?) b/(?<b>.+)$", RegexOptions.Multiline);
            // The truncation marker a caller appends is not part of the last patch.
            var patch = Regex.Replace(s, @"\n\[diff truncated at \d+ (?:characters|bytes)\]\s*$", "\n").TrimEnd() + "\n";
            return new FilePatch(header.Success ? header.Groups["b"].Value : "(unknown)", patch, Regex.IsMatch(s, @"(?m)^(?:new file mode|new mode|index \S+) 120000"));
        }).ToList();
    }

    private static async Task<string?> Git(string directory, string? stdin, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git", ["-C", directory, .. args])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var error = p.StandardError.ReadToEndAsync(ct);
        if (stdin is not null)
            await p.StandardInput.WriteAsync(stdin.AsMemory(), ct);
        p.StandardInput.Close();
        await p.WaitForExitAsync(ct);
        await error;
        return p.ExitCode == 0 ? (await output).Trim() : null;
    }
}

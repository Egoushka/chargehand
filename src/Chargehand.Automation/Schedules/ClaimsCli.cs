using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Chargehand.Automation;

/// <summary><c>chargehand claims check [--root &lt;dir&gt;] [--changed-since &lt;ref&gt;] [--test-list &lt;file&gt;] [--json]</c> (specs/2026-11-11-claim-drift-watch.md): checks that what docs/guide cites still exists.
/// Profile-free, no network, no model. Exit 0 clean, 1 findings (one line each), 2 usage or git error.</summary>
public static class ClaimsCli
{
    public const string Usage = "usage: chargehand claims check [--root <dir>] [--changed-since <ref>] [--test-list <file>] [--json]";

    public static async Task<int> RunAsync(IReadOnlyList<string> args, string workingDirectory, TextWriter output, TextWriter error, CancellationToken ct)
    {
        string? root = null, since = null, testList = null;
        var json = false;
        if (args is not ["check", ..])
            return Fail(error, Usage);
        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--root" when i + 1 < args.Count: root = args[++i]; break;
                case "--changed-since" when i + 1 < args.Count: since = args[++i]; break;
                case "--test-list" when i + 1 < args.Count: testList = args[++i]; break;
                case "--json": json = true; break;
                default: return Fail(error, Usage);
            }
        }
        root = Path.GetFullPath(root ?? workingDirectory, workingDirectory);
        if (!Directory.Exists(root) || testList is not null && !File.Exists(testList))
            return Fail(error, $"claims: {(Directory.Exists(root) ? testList : root)} does not exist");
        var claims = ClaimDrift.Scan(root);
        if (since is not null)
        {
            var changed = await ChangedAsync(root, since, ct);
            if (changed is null)
                return Fail(error, $"claims: git could not list the files changed since {since}");
            claims = [.. claims.Where(c => c.Kind == ClaimKind.Malformed ? changed.Contains(c.DocFile) : changed.Contains(c.Path))];
        }
        var tests = testList is null ? null : ClaimDrift.TestNames(await File.ReadAllTextAsync(testList, ct));
        var findings = ClaimDrift.Check(claims, root, tests);
        if (json)
            output.WriteLine(Json(findings));
        else
            foreach (var finding in findings)
                output.WriteLine(finding.Line);
        return findings.Count == 0 ? 0 : 1;
    }

    private static string Json(IReadOnlyList<DriftFinding> findings) => new JsonArray([.. findings.Select(f => (JsonNode)new JsonObject
    {
        ["doc"] = f.DocFile,
        ["line"] = f.DocLine,
        ["reason"] = f.ReasonName,
        ["path"] = f.CitedPath,
        ["detail"] = f.Detail,
    })]).ToJsonString();

    private static async Task<HashSet<string>?> ChangedAsync(string root, string since, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "diff", "--name-only", $"{since}..HEAD", "--" })
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi);
        if (process is null)
            return null;
        var text = await process.StandardOutput.ReadToEndAsync(ct);
        await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0 ? [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())] : null;
    }

    private static int Fail(TextWriter error, string message)
    {
        error.WriteLine(message);
        return 2;
    }
}

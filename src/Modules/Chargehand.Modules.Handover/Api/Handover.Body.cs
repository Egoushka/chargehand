using System.Text;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Handover.Api;

public sealed partial class Handover
{
    private static string DescribeBody(string body, List<string> changed, VerificationRecord? verification, IReadOnlyList<string> verificationPaths, string? policyNote, TestIntegrityFlag? integrity,
        string? noTestsRule)
    {
        var text = new StringBuilder();
        if (integrity is not null)
            text.Append(TestIntegrity.Warning(integrity)).Append("\n\n");
        text.Append(body.TrimEnd()).Append("\n\n---\n\n");
        // A flagged pull request is never merged, so the opted-in text about auto-merge would be untrue.
        text.Append(integrity is not null && policyNote is not null ? "Opened by chargehand as a draft. Because of the warning above, chargehand marks it ready for review and does not merge it." : policyNote ?? "Opened by chargehand as a draft; it is not merged and chargehand will not merge it.").Append("\n\n");
        text.Append(verification is null
            ? $"Verification: no test command was found, so nothing was run.{(noTestsRule is null ? "" : $" It was pushed because {noTestsRule}.")}\n"
            : string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Verification (chargehand's own run in a fresh container, {VerificationSourceText(verification.Source)}): `{string.Join(' ', verification.Argv)}` exited {verification.ExitCode} in {verification.Duration.TotalSeconds:0}s.\n"));
        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"Changed files: {changed.Count}.\n");
        if (verificationPaths.Count > 0)
            text.Append("\nThe change touches files that decide what the tests mean; read them first: ").Append(string.Join(", ", verificationPaths.Take(20))).Append(".\n");
        return text.ToString();
    }

    /// <summary>Where the verification command came from, for the pull request body (CHARGEHAND-431): the requester cannot assume their own
    /// <c>context.verify</c> ran unchanged, since chargehand refuses one that checks nothing.</summary>
    private static string VerificationSourceText(string source) => source switch
    {
        "request" => "the command the task named",
        "ci" => "read from the repository's own CI workflow",
        _ => "detected from the repository",
    };

    /// <summary>What to tell the requester when chargehand found no command to verify the change with and could derive none: whether the
    /// repository has a CI workflow chargehand could not read a test command out of, or none at all (CHARGEHAND-431).</summary>
    private static string NoTestCommandMessage(string directory) =>
        $"no test command found for this repository, and {(RepositoryReadiness.CiPresent(directory) ? "chargehand could not derive one from its CI workflow" : "it has no CI configuration either")}; " +
        "chargehand does not push a change it cannot test (name one in the request's context.verify)";
}

namespace Chargehand.Modules.Oversight.Api;

/// <summary>The kinds of human fix issue #452 found in chargehand's own merged pull requests: lint or format, a missing import or using, an unbounded or untrusted input, and a CI
/// configuration edit. <see cref="Other"/> is every human commit the classifier does not recognise.</summary>
public static class HumanEditCategory
{
    public const string Lint = "lint";
    public const string MissingImport = "missing_import";
    public const string UnboundedInput = "unbounded_input";
    public const string CiConfig = "ci_config";
    public const string Other = "other";
}

/// <summary>Sorts one human commit added to a pull request after chargehand's own last push into a <see cref="HumanEditCategory"/>. A path under a CI configuration directory decides
/// first and always, the same paths a driven run itself must not touch (CLAUDE.md); after that, the commit message's own words decide. Pure and deterministic, so the fixture commits
/// a classification test feeds it need no forge, no git checkout and no network.</summary>
public static class HumanEditClassifier
{
    private static readonly string[] CiPaths =
        [".github/workflows/", ".github/actions/", ".gitlab-ci.yml", "azure-pipelines.yml", ".circleci/", "Jenkinsfile", ".buildkite/", "bitbucket-pipelines.yml", ".drone.yml"];

    private static readonly string[] LintWords = ["lint", "format", "formatting", "whitespace", "trailing space", "style nit", "unused using", "unused import"];

    private static readonly string[] MissingImportWords = ["missing import", "missing using", "add import", "add using", "add missing import", "add missing using", "unresolved reference", "undefined reference"];

    private static readonly string[] UnboundedInputWords =
        ["unbounded", "truncate", "truncation", "max length", "maxlength", "size limit", "size cap", "overflow", "untrusted input", "unvalidated input", "validate input", "input size", "bound the input", "cap the input"];

    public static string Classify(string message, IReadOnlyList<string> changedPaths)
    {
        if (changedPaths.Any(IsCiPath))
            return HumanEditCategory.CiConfig;
        var lower = message.ToLowerInvariant();
        if (ContainsAny(lower, LintWords))
            return HumanEditCategory.Lint;
        if (ContainsAny(lower, MissingImportWords))
            return HumanEditCategory.MissingImport;
        if (ContainsAny(lower, UnboundedInputWords))
            return HumanEditCategory.UnboundedInput;
        return HumanEditCategory.Other;
    }

    private static bool IsCiPath(string path) => CiPaths.Any(ci => path.Contains(ci, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAny(string haystack, string[] needles) => needles.Any(haystack.Contains);
}

using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>The backstop before a push (ADR 0039): what a session's diff may not carry out to a remote. It is a pattern list, not a guarantee.</summary>
public class DiffScanTests
{
    // Built at run time so this file holds no string a secret scanner would flag.
    private static readonly string AwsKey = "AKIA" + "IOSFODNN7EXAMPLE";
    private static readonly string PrivateKey = "-----BEGIN " + "RSA PRIVATE KEY-----";
    private static readonly string GithubToken = "gh" + "p_" + new string('a', 36);
    private static readonly string AnthropicKey = "sk-" + "ant-" + new string('b', 30);

    private static string Diff(params string[] added) =>
        "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n@@ -0,0 +1 @@\n" + string.Join("", added.Select(l => $"+{l}\n"));

    [Fact]
    public void A_clean_diff_has_no_findings() =>
        Assert.Empty(DiffScan.Scan(Diff("var retries = 3;", "// retry twice"), ["not-in-the-diff-secret"], ["src/a.cs", "README.md"]));

    [Theory]
    [InlineData("aws")]
    [InlineData("private-key")]
    [InlineData("github-token")]
    [InlineData("anthropic-key")]
    public void Known_secret_shapes_in_added_lines_are_findings_named_without_their_value(string kind)
    {
        var line = kind switch { "aws" => AwsKey, "private-key" => PrivateKey, "github-token" => GithubToken, _ => AnthropicKey };
        var findings = DiffScan.Scan(Diff("var x = 1;", $"key = \"{line}\""), [], ["f.txt"]);
        var finding = Assert.Single(findings);
        Assert.Equal(kind, finding.Kind);
        Assert.DoesNotContain(line, finding.ToString());
        Assert.Equal("f.txt", finding.Path);
    }

    [Fact]
    public void Only_added_lines_count()
    {
        var removed = "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n@@ -1 +0,0 @@\n-" + AwsKey + "\n";
        Assert.Empty(DiffScan.Scan(removed, [], ["f.txt"]));
        Assert.Empty(DiffScan.Scan("+++ b/f.txt\n context " + AwsKey + "\n", [], ["f.txt"]));
    }

    [Fact]
    public void A_credential_literal_is_a_finding_whatever_its_shape()
    {
        var findings = DiffScan.Scan(Diff("token = plain-secret-value-42"), ["plain-secret-value-42"], ["f.txt"]);
        Assert.Equal("credential-literal", Assert.Single(findings).Kind);
        Assert.Empty(DiffScan.Scan(Diff("token = short"), ["short"], ["f.txt"]));               // under 8 characters: too likely to match by accident
    }

    [Theory]
    [InlineData(".env", true)]
    [InlineData("config/.env.production", true)]
    [InlineData(".env.example", false)]
    [InlineData("deploy/id_rsa", true)]
    [InlineData("keys/server.pem", true)]
    [InlineData("certs/client.pfx", true)]
    [InlineData("src/environment.cs", false)]
    [InlineData("docs/keys.md", false)]
    public void A_secret_like_file_name_is_a_finding(string path, bool finding) =>
        Assert.Equal(finding, DiffScan.Scan("", [], [path]).Any(f => f.Kind == "secret-file"));

    [Theory]
    [InlineData(".github/workflows/ci.yml", true)]
    [InlineData(".github/actions/setup/action.yml", true)]
    [InlineData(".gitlab-ci.yml", true)]
    [InlineData("azure-pipelines.yml", true)]
    [InlineData(".circleci/config.yml", true)]
    [InlineData("Jenkinsfile", true)]
    [InlineData(".github/CODEOWNERS", false)]
    [InlineData("docs/ci.md", false)]
    public void A_change_to_ci_configuration_is_named_because_pushing_it_can_run_it(string path, bool ci) =>
        Assert.Equal(ci, DiffScan.CiPaths([path, "src/a.cs"]).Contains(path));
}

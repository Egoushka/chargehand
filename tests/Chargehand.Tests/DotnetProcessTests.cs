using Chargehand.Warming;

namespace Chargehand.Tests;

public sealed class DotnetProcessTests
{
    [Fact]
    public async Task The_child_gets_an_allowlisted_environment_and_a_private_home()
    {
        Environment.SetEnvironmentVariable("CHARGEHAND_PROMOTER_TEST_SECRET", "x");
        try
        {
            using var root = new TempDir();
            var script = "test -z \"$CHARGEHAND_PROMOTER_TEST_SECRET\" && test -n \"$PATH\" && test \"$DOTNET_CLI_TELEMETRY_OPTOUT\" = 1 "
                + "&& test \"$MSBUILDDISABLENODEREUSE\" = 1 && test \"$HOME\" != \"$ORIGINAL\" && test -d \"$HOME\" && test \"$DOTNET_CLI_HOME\" = \"$HOME\" && echo \"$HOME\"";
            var (code, output) = await DotnetProcess.RunAsync("/bin/sh", ["-c", script.Replace("$ORIGINAL", Environment.GetEnvironmentVariable("HOME") ?? "", StringComparison.Ordinal)], root.Path, TimeSpan.FromSeconds(30), default);

            Assert.Equal(0, code);
            Assert.False(Directory.Exists(output.Trim()), "the private home is removed afterwards");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHARGEHAND_PROMOTER_TEST_SECRET", null);
        }
    }

    [Fact]
    public void A_proxy_and_certificate_setup_reaches_the_child_so_signature_checks_work_behind_a_proxy()
    {
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "SSL_CERT_FILE", "SSL_CERT_DIR" })
            Assert.Contains(name, DotnetProcess.Inherited);
    }

    [Fact]
    public async Task A_run_past_its_timeout_is_killed_and_returns_minus_one()
    {
        using var root = new TempDir();
        var started = DateTime.UtcNow;
        var (code, _) = await DotnetProcess.RunAsync("/bin/sh", ["-c", "sleep 30"], root.Path, TimeSpan.FromMilliseconds(300), default);

        Assert.Equal(-1, code);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Only_the_tail_of_a_long_output_is_kept()
    {
        using var root = new TempDir();
        var (code, output) = await DotnetProcess.RunAsync("/bin/sh", ["-c", "head -c 100000 /dev/zero | tr '\\0' a; echo END"], root.Path, TimeSpan.FromSeconds(30), default);

        Assert.Equal(0, code);
        Assert.True(output.Length <= 8192, $"output was {output.Length} chars");
        Assert.EndsWith("END", output.TrimEnd());
    }
}

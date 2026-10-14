using System.Text.RegularExpressions;

namespace Chargehand.Tests;

/// <summary>ADR 0058: the release workflow publishes the session images for linux/amd64 and linux/arm64. The workflow cannot run here; these checks pin its shape so a later edit does not drop the arm64 images or
/// the approval they sit behind.</summary>
public sealed partial class ReleaseWorkflowTests
{
    private static readonly string[] Lines = File.ReadAllLines(Repo.Path(".github", "workflows", "release.yml"));

    /// <summary>The lines of one top-level job.</summary>
    private static string[] Job(string name)
    {
        var start = Array.FindIndex(Lines, l => l == $"  {name}:");
        Assert.True(start >= 0, $"no job {name}");
        var end = Array.FindIndex(Lines, start + 1, l => JobStart().IsMatch(l));
        return Lines[start..(end < 0 ? Lines.Length : end)];
    }

    [GeneratedRegex(@"^  [a-z][a-z0-9-]*:\s*$")]
    private static partial Regex JobStart();

    [Fact]
    public void The_arm64_job_runs_natively_on_an_arm_runner_after_the_release_job_without_asking_for_another_approval()
    {
        var job = Job("session-arm64");

        Assert.Contains("    needs: release", job);
        Assert.Contains(job, l => Regex.IsMatch(l, @"^    runs-on: ubuntu-.*-arm$"));
        Assert.DoesNotContain(job, l => l.StartsWith("    environment:", StringComparison.Ordinal));      // the release job's approval is the only one
        Assert.Contains("      packages: write", job);
        // Not emulated: no QEMU, no --platform flag on the build.
        Assert.DoesNotContain(job, l => l.Contains("qemu", StringComparison.OrdinalIgnoreCase) || l.Contains("--platform", StringComparison.Ordinal));
    }

    [Fact]
    public void Each_session_image_tag_becomes_an_index_of_the_amd64_image_from_the_release_job_and_the_arm64_one_built_here()
    {
        var text = string.Join('\n', Job("session-arm64"));

        Assert.Contains("docker buildx imagetools create -t \"$SESSION_IMAGE:$VERSION\" \"$SESSION_AMD64\" \"$SESSION_ARM64\"", text);
        Assert.Contains("docker buildx imagetools create -t \"$SESSION_JDK_IMAGE:$VERSION\" \"$SESSION_JDK_AMD64\" \"$SESSION_JDK_ARM64\"", text);
        // The JDK image is built on the arm64 session image by digest, as the amd64 one is on its own.
        Assert.Contains("--build-arg \"BASE=$SESSION_ARM64\"", text);
        var release = string.Join('\n', Job("release"));
        Assert.Contains("session_amd64: ${{ steps.digests.outputs.session }}", release);
        Assert.Contains("session_jdk_amd64: ${{ steps.digests.outputs.session_jdk }}", release);
    }

    [Fact]
    public void The_new_job_pins_its_actions_and_keeps_expressions_out_of_shell_commands()
    {
        var job = Job("session-arm64");
        var inRun = false;
        foreach (var line in job)
        {
            var indent = line.Length - line.TrimStart().Length;
            if (inRun && (line.Trim().Length == 0 || indent > 8))
            {
                Assert.DoesNotContain("${{", line);
                continue;
            }
            inRun = false;
            var run = Regex.Match(line, @"^\s*(- )?run:\s*(?<rest>.*)$");
            if (run.Success)
            {
                Assert.DoesNotContain("${{", run.Groups["rest"].Value);
                inRun = run.Groups["rest"].Value is "|" or "|-";
            }
            var uses = Regex.Match(line, @"uses:\s*(?<ref>\S+)");
            if (uses.Success)
                Assert.Matches(@"@[0-9a-f]{40}$", uses.Groups["ref"].Value);
        }
    }

    [Fact]
    public void The_mcp_registry_job_still_follows_the_release_job()
    {
        Assert.Contains("    needs: release", Job("mcp-registry"));
    }
}

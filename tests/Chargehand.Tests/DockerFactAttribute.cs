using System.Diagnostics;

namespace Chargehand.Tests;

/// <summary>Runs a test only when a Docker engine answers and CHARGEHAND_TEST_IMAGE names a local image by digest; else skipped, with the reason.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public static string? Image { get; } = Environment.GetEnvironmentVariable("CHARGEHAND_TEST_IMAGE");

    public DockerFactAttribute() => Skip = Reason(egress: false);

    /// <summary>Why a Docker test cannot run here, or null when it can.</summary>
    internal static string? Reason(bool egress)
    {
        if (string.IsNullOrEmpty(Image))
            return "set CHARGEHAND_TEST_IMAGE to a local image reference (name@sha256:...) to run Docker tests";
        if (egress && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CHARGEHAND_TEST_EGRESS_IMAGE")))
            return "set CHARGEHAND_TEST_EGRESS_IMAGE (scripts/egress-test-image.sh <aspnet-base-image>) to run the egress tests";
        return EngineAnswers() ? null : "no Docker engine answered `docker info`";
    }

    private static bool EngineAnswers()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("docker", "info") { RedirectStandardOutput = true, RedirectStandardError = true })!;
            return p.WaitForExit(15_000) && p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>A <see cref="DockerFactAttribute"/> test that also needs the egress test image.</summary>
public sealed class DockerEgressFactAttribute : FactAttribute
{
    public DockerEgressFactAttribute() => Skip = DockerFactAttribute.Reason(egress: true);
}

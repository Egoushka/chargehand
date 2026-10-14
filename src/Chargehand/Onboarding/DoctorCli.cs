namespace Chargehand.Onboarding;

/// <summary><c>chargehand doctor [--profile &lt;path&gt;]</c>: one line per check, <c>ok</c>, <c>warn</c> or <c>fail</c> with the fix. Exit 0 when no check fails, 1 when one does,
/// 2 on a usage error. It needs no working profile: a profile that does not load is its first failing line.</summary>
public static class DoctorCli
{
    public const string Usage = "usage: chargehand doctor [--profile <path>]";

    /// <param name="profilePath">The path the global profile option or environment names; <c>--profile</c> after the command wins.</param>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, string profilePath, IDoctorEnvironment env, IReadOnlyList<string> presetAllow, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (args is ["--profile", var path])
            profilePath = path;
        else if (args.Count > 0)
        {
            error.WriteLine(Usage);
            return 2;
        }
        var results = await Doctor.RunAsync(profilePath, env, presetAllow, ct);
        foreach (var result in results)
            output.WriteLine(Doctor.Format(result));
        return Doctor.ExitCode(results);
    }
}

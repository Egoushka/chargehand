using System.Text;
using System.Text.RegularExpressions;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Modules.Handover.Api;

/// <summary>git in the scratch clone: a clean environment, the push's credential, and the remotes chargehand pushes to.</summary>
public sealed partial class Handover
{
    /// <summary>The whole <c>git push</c>: one ref, never forced (no <c>+</c>), never a mirror or a delete. The credential is not here.</summary>
    public static IReadOnlyList<string> PushArgs(string remoteUrl, string branch) =>
        ["push", "--porcelain", remoteUrl, $"refs/heads/{branch}:refs/heads/{branch}"];

    /// <summary>The environment of a push: nothing is inherited from the user's git configuration or askpass, and the credential is an HTTP header through git's
    /// environment configuration (or an ssh key file), so it appears on no command line.</summary>
    public static IReadOnlyDictionary<string, string> PushEnvironment(PushCredential? credential, string remoteUrl, string scratchRoot, IReadOnlyDictionary<string, string>? baseEnvironment = null)
    {
        var env = new Dictionary<string, string>(baseEnvironment ?? GitProcess.Environment(scratchRoot), StringComparer.Ordinal);
        if (credential is null)
            return env;
        if (credential.Kind == PushCredentialKind.HttpsToken)
        {
            env["GIT_CONFIG_COUNT"] = "1";
            env["GIT_CONFIG_KEY_0"] = "http.extraheader";
            env["GIT_CONFIG_VALUE_0"] = $"Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{credential.Value}"))}";
        }
        else
        {
            var keyFile = Path.Combine(scratchRoot, $"push-key-{Guid.NewGuid():N}");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var writer = new StreamWriter(new FileStream(keyFile, options)))
                writer.Write(credential.Value.TrimEnd() + "\n");
            env["GIT_SSH_COMMAND"] = $"ssh -i {keyFile} -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes -o BatchMode=yes";
        }
        return env;
    }

    internal static bool RemoteAllowed(string url, bool allowLocal) =>
        url.Length > 0 && !url.StartsWith('-')
        && (url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("ssh://", StringComparison.Ordinal) || ScpForm().IsMatch(url)
            || (allowLocal && url.StartsWith("file://", StringComparison.Ordinal)));

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+@[A-Za-z0-9_.-]+:[A-Za-z0-9_./-]+$")]
    private static partial Regex ScpForm();
}

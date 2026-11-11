using Chargehand.Modules.Runners.Api;

namespace Chargehand.Config;

/// <summary>The runner-facing reads of <c>driven</c>, each a thin call into <see cref="DrivenRunnerSet"/> (Runners, ADR 0060) until the hosts compose the root.</summary>
public sealed partial record DrivenSettings
{
    /// <summary>The name of the runner that <c>driven.runner</c> declares.</summary>
    public const string DefaultRunnerName = DrivenRunnerSet.DefaultRunnerName;

    /// <summary>The runners this server may use (ADR 0058), in the order the profile lists them: <c>runners</c>, else <c>runner</c> as one named <see cref="DefaultRunnerName"/>, else none (the server calls the engine itself).</summary>
    public IReadOnlyList<DrivenRunnerEntry> ResolvedRunners() => DrivenRunnerSet.Resolve(Runner, Runners);

    /// <summary>True when the profile uses <c>runners</c>: run events and the batch result then say which runner a session ran on.</summary>
    public bool NamesRunners => DrivenRunnerSet.Names(Runners);

    /// <summary>The image to start on a runner of <paramref name="platform"/>: the reference as the profile writes it, or its variant for that platform (<c>image_variants</c>).</summary>
    public string ImageFor(string reference, string platform) => DrivenRunnerSet.ImageFor(ImageVariants, reference, platform);

    /// <summary>Every image of <c>images</c> as a runner of <paramref name="platform"/> starts it: what that runner's allowlist must carry (<see cref="ImageAllowlist"/>).</summary>
    public IReadOnlyList<string> SessionImagesFor(string platform) => DrivenRunnerSet.SessionImagesFor(Images, ImageVariants, platform);

    /// <summary>The environments a profile may name (<c>driven.environment</c>); <c>image</c> is the default.</summary>
    public static IReadOnlyList<string> Environments => DrivenRunnerSet.Environments;

    /// <summary>ADR 0078: toolchains come from mise, installed per repository before each session, not from the session image.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesMise => DrivenRunnerSet.UsesMise(Environment);

    internal void ValidateEnvironment() => DrivenRunnerSet.ValidateEnvironment(Environment);

    internal void ValidateRunners() => DrivenRunnerSet.ValidateRunners(Runner, Runners, Network, ImageVariants, Images);
}

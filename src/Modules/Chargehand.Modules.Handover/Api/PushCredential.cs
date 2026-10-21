using Chargehand.Modules.Forge.Api;

namespace Chargehand.Modules.Handover.Api;

public enum PushCredentialKind { HttpsToken, SshKey }

/// <summary>What pushes a branch: a fine-grained token or a deploy key that cannot push the default branch, or a GitHub App installation token minted for each push
/// (<paramref name="Mint"/>; <paramref name="Value"/> is then empty). It is read from a secret source at push time and is never inside a container.</summary>
public sealed record PushCredential(string Value, PushCredentialKind Kind = PushCredentialKind.HttpsToken, Func<CancellationToken, Task<string>>? Mint = null)
{
    /// <summary>An https token asked of <paramref name="mint"/> each time it is used.</summary>
    public static PushCredential Minted(Func<CancellationToken, Task<string>> mint) => new("", PushCredentialKind.HttpsToken, mint);

    /// <summary>The token to use now: the stored one, or a token from <see cref="Mint"/>.</summary>
    /// <exception cref="GitHubAppException">The App's token could not be had; the message carries no token.</exception>
    public async Task<PushCredential> CurrentAsync(CancellationToken ct) => Mint is null ? this : new PushCredential(await Mint(ct), Kind);

    /// <summary>The token's value now, for an API call made with it.</summary>
    public async Task<string> TokenAsync(CancellationToken ct) => (await CurrentAsync(ct)).Value;
}

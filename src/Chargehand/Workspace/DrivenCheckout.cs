namespace Chargehand.Workspace;

/// <summary>chargehand's own checkout of the request's commit and where it came from.</summary>
public sealed record DrivenCheckout(string Directory, string Commit, string RemoteUrl, string BaseBranch);

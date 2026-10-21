using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Journal.Api;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Modules.Handover.Api;

/// <summary>The verification, the push and the pull request of a handover, each a recorded step when the handover has a journal (ADR 0061).</summary>
public sealed partial class Handover
{
    /// <summary>The verification as step <c>handover/&lt;run&gt;/verify</c>, redacted before it is recorded. A run id has one bundle, so its recorded verdict, passed or failed, stands for the
    /// branch: a resumed handover does not verify again. A verifier that throws is recorded as a failure, so the next handover verifies again.</summary>
    private async Task<VerificationRecord> VerifyAsync(BranchVerification verification, string source, IReadOnlyList<string> secrets, CancellationToken ct)
    {
        async Task<VerificationRecord> Verify(StepAttempt attempt, CancellationToken token)
        {
            var raw = await verifier.VerifyAsync(verification, token);
            return raw with { Source = source, OutputTail = Redact(raw.OutputTail, secrets) };
        }
        return steps is null ? await Verify(new StepAttempt(1, false), ct) : await steps.RunAsync(StepKey.Verify(batchId!, verification.RunId), Verify, HandoverSteps.Default.VerificationRecord, ct);
    }

    /// <summary>The push as step <c>handover/&lt;run&gt;/push</c>. Run again, it asks the remote first: a branch that already holds the commit is taken as pushed, so it is not pushed twice.</summary>
    private async Task<PushedBranch> PushAsync(HandoverInput input, string scratch, string commit, IReadOnlyDictionary<string, string> env, List<string> secrets, CancellationToken ct)
    {
        async Task<PushedBranch> Push(StepAttempt attempt, CancellationToken token)
        {
            for (var tried = 1; ; tried++)
            {
                // A minted token is asked for at each attempt, so a long session or a retry never pushes with one that has lapsed.
                var credential = input.Credential is { } stored ? await stored.CurrentAsync(token) : null;
                if (credential is { Value.Length: > 0 } && !secrets.Contains(credential.Value))
                    secrets.Add(credential.Value);
                var pushEnv = PushEnvironment(credential, input.RemoteUrl, input.ScratchRoot, env);
                // A push that did not get an answer may still have landed.
                if (attempt.Repeated || tried > 1)
                {
                    var remote = await GitProcess.RunAsync(scratch, pushEnv, ["ls-remote", "--", input.RemoteUrl, $"refs/heads/{input.Branch}"], GitTimeout, token);
                    if (remote.ExitCode == 0 && remote.Stdout.Split('\t', 2)[0].Trim() == commit)
                        return new PushedBranch(input.Branch, commit, Adopted: true);
                }
                var push = await GitProcess.RunAsync(scratch, pushEnv, PushArgs(input.RemoteUrl, input.Branch), GitTimeout, token);
                if (push.ExitCode == 0)
                    return new PushedBranch(input.Branch, commit);
                if (tried > PushPauses.Count || !Unreachable(push.Tail))
                    throw new PushRejectedException(tried == 1 ? push.Tail : $"{push.Tail} (after {tried} attempts)");
                await _pause(PushPauses[tried - 1], token);
            }
        }
        return steps is null ? await Push(new StepAttempt(1, false), ct) : await steps.RunAsync(StepKey.Push(batchId!, input.RunId), Push, HandoverSteps.Default.PushedBranch, ct);
    }

    /// <summary>The draft as step <c>handover/&lt;run&gt;/pr</c>, with the body it was opened with. Run again, it looks the branch's open pull request up first and opens one only if there is none.</summary>
    private async Task<(PullRequestRef Pr, string Body)> OpenPullRequestAsync(HandoverInput input, RemoteRepository repository, string body, CancellationToken ct)
    {
        // The last paragraph of the body: the squash merge's message is built from the body, and git reads trailers only at the end of a message.
        var trailers = input.Provenance is { } provenance ? $"\n\n{provenance.Block()}\n" : "";
        var fallbackBody = input.FallbackBaseBranch is { } fallback
            ? $"{body.TrimEnd()}\n\nThis pull request is based on `{fallback}`, not on `{input.BaseBranch}`: the host did not accept that branch as a base (it may be gone). The branch holds the earlier task's work as well.{trailers}"
            : body;
        body = body.TrimEnd() + trailers;
        async Task<OpenedPullRequest> Open(StepAttempt attempt, CancellationToken token)
        {
            if (attempt.Repeated && await pullRequests.FindByHeadAsync(repository, input.Branch, token) is { } existing)
                return new OpenedPullRequest(existing with { Reused = true }, false);
            try
            {
                return new OpenedPullRequest(await pullRequests.CreateDraftAsync(repository, input.Branch, input.BaseBranch, input.Title, body, token), false);
            }
            catch (PullRequestException) when (input.FallbackBaseBranch is { } fallback && fallback != input.BaseBranch)
            {
                return new OpenedPullRequest(await pullRequests.CreateDraftAsync(repository, input.Branch, fallback, input.Title, fallbackBody, token), true);
            }
        }
        var opened = steps is null ? await Open(new StepAttempt(1, false), ct) : await steps.RunAsync(StepKey.PullRequest(batchId!, input.RunId), Open, HandoverSteps.Default.OpenedPullRequest, ct);
        return (opened.PullRequest, opened.OnFallback ? fallbackBody : body);
    }

    /// <summary>The pauses before a push that did not reach the remote is tried again: a session's finished work is not dropped for a network fault of a minute or two.</summary>
    public static readonly IReadOnlyList<TimeSpan> PushPauses = [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90)];

    /// <summary>The push never got an answer from the remote: it could not connect or resolve it, or the remote answered with a server error. A refusal (4xx, a hook, a rule) is final.</summary>
    public static bool Unreachable(string tail) => UnreachableRemote().IsMatch(tail);

    [GeneratedRegex(@"unable to access '[^']*': (?!The requested URL returned error: 4)|ssh: connect to host |ssh: Could not resolve hostname ")]
    private static partial Regex UnreachableRemote();

    private sealed class PushRejectedException(string tail) : Exception(tail);
}

/// <summary>The recorded result of a handover's push: <paramref name="Adopted"/> when a repeated push found the commit already on the remote.</summary>
internal sealed record PushedBranch(string Branch, string Commit, bool Adopted = false);

/// <summary>The recorded result of a handover's pull request: <paramref name="OnFallback"/> when it was opened against the fallback base.</summary>
internal sealed record OpenedPullRequest(PullRequestRef PullRequest, bool OnFallback);

[JsonSerializable(typeof(VerificationRecord))]
[JsonSerializable(typeof(PushedBranch))]
[JsonSerializable(typeof(OpenedPullRequest))]
internal sealed partial class HandoverSteps : JsonSerializerContext;

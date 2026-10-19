using System.Text.Json.Serialization;
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
    private async Task<PushedBranch> PushAsync(HandoverInput input, string scratch, string commit, IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        async Task<PushedBranch> Push(StepAttempt attempt, CancellationToken token)
        {
            var pushEnv = PushEnvironment(input.Credential, input.RemoteUrl, input.ScratchRoot, env);
            if (attempt.Repeated)
            {
                var remote = await GitProcess.RunAsync(scratch, pushEnv, ["ls-remote", "--", input.RemoteUrl, $"refs/heads/{input.Branch}"], GitTimeout, token);
                if (remote.ExitCode == 0 && remote.Stdout.Split('\t', 2)[0].Trim() == commit)
                    return new PushedBranch(input.Branch, commit, Adopted: true);
            }
            var push = await GitProcess.RunAsync(scratch, pushEnv, PushArgs(input.RemoteUrl, input.Branch), GitTimeout, token);
            return push.ExitCode == 0 ? new PushedBranch(input.Branch, commit) : throw new PushRejectedException(push.Tail);
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

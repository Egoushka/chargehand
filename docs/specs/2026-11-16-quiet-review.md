# Quiet review mode

Status: accepted (owner's ask, 2026-11-16).

## Problem

`review_pulls` off stops the review comment on every agent-authored pull request, but the merge gate needs chargehand's own clean review of the head, so with it off nothing merges (`MergeChainGaps` warns). The comment is the only place the clean verdict lives.

## Design

New `automation.github.review_comments` (default `true`). With `review_pulls` on and `review_comments` off:

- A completed review with no blocking finding and a whole diff records `owner/repo#N@sha` in `IntakeState.Cleared` and posts nothing. The merge gate and the review backlog read `Cleared` beside the clean comment.
- A blocking finding posts the usual single comment (no clean marker) and starts the review round as before; nothing merges.
- A failed or stopped review posts nothing and logs one warning; the head is not cleared.
- A truncated diff clears nothing (as today) and still labels the pull request for the owner.
- `Cleared` is local state written by this server only; a lost state file re-reviews, never merges unreviewed. A push is a new sha and is not covered.

No new GitHub permission is needed. Rejected: a commit status or check run (needs the App's `statuses`/`checks` write permission and still shows on the PR).

## Acceptance checks (machine-run)

`dotnet test tests/Chargehand.Tests --filter "FullyQualifiedName~ReviewMergeTests|FullyQualifiedName~PullReviewTests"`:

1. `Quiet_review_a_clean_head_is_cleared_and_merged_with_zero_comments`: zero posted comments, the head merged.
2. `Quiet_review_a_blocking_finding_still_blocks_and_is_the_one_comment`: no merge, exactly one comment with the finding.
3. `Quiet_review_a_push_after_the_clearance_is_not_covered_by_it`.
4. The existing comment-mode tests stay green.

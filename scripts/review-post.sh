#!/usr/bin/env bash
# Posts the review comment on a pull request, or edits the one a previous run posted: the comment whose body starts with
# the marker scripts/review-comment.sh writes. .github/workflows/review.yml runs it.
#
# usage: scripts/review-post.sh <owner/repo> <pull-request-number> <comment-file>
# env: GH_TOKEN  a token that may write pull-request comments (gh reads it)
#
# Anyone who can comment can write the marker too. An edit of a comment the token does not own is refused, so the script
# tries each marked comment in turn and posts a new one only when none could be edited.
set -euo pipefail

repo=${1:?owner/repo}
pr=${2:?pull request number}
file=${3:?comment file}
marker='<!-- chargehand-review -->'

[[ $repo =~ ^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$ ]] || { echo "review-post: '$repo' is not owner/repo" >&2; exit 2; }
[[ $pr =~ ^[0-9]+$ ]] || { echo "review-post: '$pr' is not a pull request number" >&2; exit 2; }
[ "$(head -n1 "$file")" = "$marker" ] || { echo "review-post: $file does not start with the marker" >&2; exit 2; }

ids=$(gh api --paginate "repos/$repo/issues/$pr/comments" | jq -r --arg m "$marker" '.[] | select(.body | startswith($m)) | .id')
for id in $ids; do
  if gh api -X PATCH "repos/$repo/issues/comments/$id" -F "body=@$file" > /dev/null; then
    echo "review-post: edited comment $id"
    exit 0
  fi
done
gh api -X POST "repos/$repo/issues/$pr/comments" -F "body=@$file" > /dev/null
echo "review-post: posted a new comment"

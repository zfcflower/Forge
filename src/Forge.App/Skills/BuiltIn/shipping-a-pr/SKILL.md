---
name: shipping-a-pr
workflow: true
allowed-tools: pr_workflow pr_create pr_sync pr_edit git_sync git_diff git_log request_review todo
description: >-
  Use when finished work has to become a pull request, or when an existing pull request needs tending: creating it,
  bringing it up to date with its base, driving CI failures and review comments back into the code, or editing the
  description. Do not use it for work that is not being delivered as a pull request, and do not use it to merge.
---

# Shipping a pull request

The pull request tools are gated in code: a gate that fails refuses the call and names itself plus the next step.
The written guidance for each phase is served by `pr_workflow`. Read the phase, then use the tool it describes.

## Read the phase first

`pr_workflow {"phase": "<phase>"}` with one of:

| phase | when |
|---|---|
| `creating` | before `pr_create` |
| `syncing` | before `pr_sync`, or when the base moved and a conflict stopped an integration |
| `body` | before `pr_edit` |
| `checks` | when CI has to be watched; it names the checks tools |
| `review-comments` | when a review thread needs a verdict and a reply |
| `screenshots` | when a change users can see needs images in the description |

The phase text carries the details and the tool names. This skill carries the order and the non-negotiables.

## Before creating: five gates

`pr_create` opens the pull request only when all five hold; a gate that does not hold refuses the call, names itself
and says what to do next.

1. The working tree is clean, untracked files included. Every change belongs to a finished todo step; a stray file
   means a step is not done.
2. After a fresh fetch, `origin/<base>` is an ancestor of HEAD. A branch that is behind is refused: bring it up with
   `pr_sync` (phase `syncing`), then re-run the tests and read `git_diff` again, because HEAD moved.
3. The full diff was read at this exact HEAD: `git_diff` with no `paths` and `stat_only` false, and every file it
   lists was shown (a large diff is capped; read the files it names as not shown with `paths`). Clean up what it
   shows - debug output, commented-out code, scratch files, unrelated edits, local paths, secrets - and commit the
   cleanup. That moves HEAD, so read the diff again afterwards.
4. The body's `## Testing` section is non-empty and not a platitude. Write what you ran and what it showed.
5. Every grouped step of this session is `done`. Items still in the loose queue do not count and do not block: they
   are not part of this delivery. `todo {"operation": "list"}` one last time before calling `pr_create`.

A refusal is an instruction, not a race to win: fix the cause and call again. Never retry a gate into passing and
never work around it with raw git commands.

## The body

- Default: the plan summary plus a generated `## Plan` section (one entry per step with its commit). Pass `body`
  only when the summary is not what should be read.
- `## Testing` states the real commands and their results. A manual check is written as a manual check with what
  you observed. Code cannot verify that you ran anything - that is exactly why it must be true.
- `title`: one line, in the repository's own style (`git_log {"limit": 20}` shows it). Not "various fixes".
- `draft` only when the user asked for a draft.
- Change one section later with `pr_edit` (a `section` edit), not by replacing the whole body, unless you mean to
  rewrite all of it. `pr_edit` refuses a full replacement when someone else edited the body in between: combine
  both, then call again.

## After creation: checks

- Watch with the checks tools that phase `checks` names. An empty list of checks means "not registered yet", not
  "no CI". Pending, queued and unknown are not results.
- A failing check is a bug in your change until proven otherwise: read the failed job's log tail, reproduce it
  locally, fix the cause, publish, watch again. A rerun without reading the log is not a diagnosis.
- The merge state and what each one means are in phase `checks`. Never report green while a check is pending or the
  state is unknown.

## Publish, and the base moved

- `pr_sync` brings the branch up to date with the pull request's base and pushes the audited HEAD. It merges when
  the branch already has merge commits, rebases otherwise, and forces only with a fresh lease.
- It stops on conflicts and lists the files: read both sides of each (`git log -p` on each side) before editing,
  never take one side wholesale, stage the resolutions, then `git_sync {"operation": "continue"}` and call
  `pr_sync` again.
- A push that the tool refuses because the remote holds commits this checkout never had is not a retry: read those
  commits first.
- Whenever HEAD changed, everything downstream is stale: re-run the tests, read `git_diff` again (gate 3 is bound
  to HEAD), and check whether the description still matches.

## Fixes go back into the implementation

A CI failure or a review comment is not answered on the pull request. It goes back into the code:

1. Judge the review comment (phase `review-comments`): apply, push back with a reason, or clarify with a question.
   Never change code to make a comment go away.
2. Reopen the step it belongs to (the same step id - read the `todos` skill) and fix it there.
3. Review the step again with `request_review {"step_id": "<id>"}`. A `request changes` verdict means fix and
   review again; an approval is required before the step goes back to `done`.
4. Publish with `pr_sync` (it pushes even when there was nothing to integrate), then watch the checks again.
5. Keep the description true: update `## Testing` and `## Changes` when the fix changed what was run or what
   changed. Reply to the review thread only after the fix is published, then resolve it.

## Never

- Never merge a pull request.
- Never force-push the base branch, and never override a refusal with raw git.
- Never claim a run, a test or a screenshot you did not make, in the body, in a review reply or to the user.
- Never leave a review thread answered by silence.

## Done

When the checks are green, the merge state is clean, and the description is true: tell the user the pull request
URL and what remains (a human merge, a required review), then stop. The delivery ends here.
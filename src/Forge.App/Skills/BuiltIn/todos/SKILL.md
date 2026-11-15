---
name: todos
description: >-
  Use when work needs a shared plan: this skill owns the todo tool's checklist (groups of steps, the loose queue),
  the completion receipt each step carries (summary, verification, commit subject) and the final plan summary that
  becomes the pull request body. Read it before adding, promoting or finishing a step, and when a review came back
  request changes. Do not use it for an answer or a fix that needs no plan.
---

# The shared plan

The checklist is shared with the user: they see it, reorder it, queue items into it and tick items themselves. It
is a plan of **groups** (a task, one title per task) with **steps** under them, plus one **loose queue** of items
that belong to no group yet.

## Read before you write

`todo {"operation": "list"}` before you choose the next step, after every user message, and before you wrap up.
The stored checklist is the source of truth, not your memory of it: the user edits it while you work, in parallel.

## The operations

| operation | what it does |
|---|---|
| `list` | reads the whole checklist: groups with their steps and ids, and the loose queue |
| `add` | `title` plus `group` puts a step into that group (an existing group of that title is reused, a missing one is created); without `group` the item joins the loose queue; `note` and `after_id` are optional |
| `update` | changes `title`, `status`, `note` and the step fields by `id`; `status` is `pending`, `in_progress`, `paused` or `done` |
| `promote` | turns a loose item into a group of its own (`id`, plus `title` only when the group title must differ from the item title) |
| `remove` | deletes an item by `id`; for mistakes only, never for finished steps |
| `plan_summary_get` / `plan_summary_set` | reads or replaces the session's plan summary (see the last section) |

## Groups and steps

- A group title names a result ("Skill source badge in settings"), not a process ("investigate", "step 2").
- A step is verifiable on its own and roughly one commit. If you cannot write its commit subject in one line, it is
  two steps.
- Steps inside a group run in order; use `after_id` when the order is not simply append.
- Keep the number of steps honest: one per change that stands on its own, not one per file.

## The loose queue

- Items the user queued arrive loose. When you take one up, `promote` it first: it becomes the group, then `add`
  its steps with `group` set to the group title.
- Never run a loose item as if it were a step: it is an input shaped by the user, not a plan.
- Loose items you are not doing now stay where they are. Do not remove them, and do not fold them into this
  delivery.

## One step in progress

- Move the step you are working on to `in_progress`; that returns the session's previous in-progress step to
  pending, so there is never more than one.
- `paused` waits for something outside the agent (an answer, a review, a dependency). A paused step never commits.
- Finish a step before opening the next one. Do not interleave two groups unless the user asked for it.

## The completion receipt

`status done` commits every uncommitted change in the worktree as that step. So `done` is the claim "finished,
verified, ready to be its own commit". In the **same** `update` call, pass:

- `summary`: what changed and why - especially the decisions the diff cannot show (why this shape, what was
  rejected, what a future reader would otherwise re-litigate). It becomes the commit body.
- `verification`: `check → result` pairs. Several checks: a Markdown list. Name the command and its outcome, not
  "tests pass". If you ran nothing, write `not verified` - honestly.
- `commit_subject`: one imperative line in the repository's own style. Read the style first:
  `git_log {"limit": 20}`. Without it, the step title becomes the subject.

`verification` is kept on the step and in the PR plan; it is never part of the commit message. A failed commit
does not undo `done`: fix the cause and call `update status done` again.

## The mechanical gate

A step that changed code may go `done` only when both of these hold:

1. `request_review {"step_id": "<id>"}` was called for that step and its verdict line read `verdict: approve`. That
   review covers the step's own commit when it has one, and the branch plus the uncommitted tree when it does not -
   which is the normal case, because `done` is what commits the step.
2. Its `verification` is non-empty and is not `not verified`.

A step that changed no code (the tree is clean and the step produced no commit of its own) passes without a
review. Anything else - `verdict: request changes`, an unstructured review, a stopped review - is not an approval.
`request changes` is acted on: fix the findings, commit, and review the step again at the new commit.

The gate is code, not advice: the `todo` tool refuses the update and says what is missing. It can be switched off
(`preferences.workflowGatesEnabled` in `settings.json`, on by default); with it off the tool no longer refuses and
what the user asks for decides - but never write a verification you did not run.

## Fixes reopen the same step

A review finding, a failing test or a user correction after a step was done reopens **that** step id
(`status in_progress`), not a new step beside it: the new `summary` and `commit_subject` describe the fix, the
review runs again, and the earlier commit stays in history. A second step for the same work hides the churn from
the plan and from the PR body.

## The plan summary

After the last step is done, write `plan_summary_set` with `text` holding exactly three sections:

```
## Summary

## Changes

## Testing
```

- It is cumulative for the whole session, written once at the end, not per step.
- It becomes the pull request body (plus a generated `## Plan` listing every step with its commit), so `## Testing`
  must list the real commands and what they showed.
- Update it if a later fix changes what was run; an outdated `## Testing` is a false claim in the pull request.

Done steps are never deleted: the plan is the record of how the change was built, and the PR plan is generated
from it.

Next: when the last step is done, take the `shipping-a-pr` route (load it with the `skill` tool).
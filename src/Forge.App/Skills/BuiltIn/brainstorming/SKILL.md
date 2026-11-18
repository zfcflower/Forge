---
name: brainstorming
description: >-
  Use after choosing-a-workflow routes a product behavior change here: a feature, a behavior change or a bug,
  however small. It interviews in rounds (product first, then design), records the answers in a temporary task
  spec, plans with the todo tool, and implements in reviewed steps. Do not use it for a question, a read-only
  look, a typo or a prose-only edit, or for handing finished work to a pull request (shipping-a-pr).
---

# Brainstorming

A change to what the product does starts with questions and ends with reviewed steps. The point is not ceremony:
the design is agreed before a source file is touched, so it does not have to be renegotiated halfway through.

## Order of work

1. Product questions: why, for whom, what the user sees.
2. Technical questions: where the change belongs, what it costs, what it replaces.
3. Write the task spec and get the design accepted.
4. Plan in the `todo` tool (load the skill by name: `skill {"name": "todos"}`).
5. Implement step by step, every code step reviewed with `request_review`.
6. Move what lasts into the owning spec, delete the task spec, then take the `shipping-a-pr` route.

Steps 1-3 are this skill. Steps 4-5 belong to the `todos` skill. Step 6 closes this one.

## Phase 1: the product

Settle these before any technical question:

- Who is it for, and what problem does it remove for them?
- What does the user see when it works, and what do they see when there is nothing (empty), when it fails (error)
  and at the boundary (zero, one, many; very long input; slow response; no permission)?
- How will anyone know it is done - what is observable, not what is intended?
- What must not change: the behavior, defaults, wording or flow around it that people already depend on?

Facts are yours to find, not the user's to answer. Read the code, the specs and the existing behavior first;
never spend a question on something the repository already answers. Ask only decisions that belong to the user:
a preference between valid designs, a requirement that is missing, a cost they should choose.

## Phase 2: the design

- Which module and which spec owns this? The specs are plain Markdown in the repository: `architecture.md` and
  `goal-and-requirements.md` at the root, `src/<Module>/SPEC.md` per module. The read-only `spec_get`/`spec_graph`/`spec_validate` tools exist only when the orchestration is on or the
  workspace already has specs with an `id`; otherwise `grep` and `read` find and read them (the `read` tool only accepts paths inside the workspace), `edit` and `write` to
  change them. New spec files start with a frontmatter block: `id` (stable, unique), `type`, `title`, `status`, and, when they
  apply, `parent`, `depends-on`, `references`, `implements`, `covers`, `tags`. Give every spec you create an `id` so the spec
  tools can see it.
- What is the smallest design that satisfies the product answers? Prefer extending what exists over adding a
  parallel implementation; name the code you will reuse.
- What did you reject, and why, in one line each? A design without a rejected alternative is usually a guess.
- How will it be verified: the command that has to pass, and the test project that should cover it. "It builds" is
  not a verification for a behavior change.
- What constraints does the repository impose: Native AOT in the main app, no new package without the user's
  agreement, the existing layering and naming. Read them out of the repo/specs instead of asking.

If reading turns into a lot of ground, delegate it: `spawn_agent` with kind `Scout` (read-only) for "where does X
live and who calls it", or `Plan` for a second opinion on the design. You still own the decision.

## How to ask

`ask_user` takes one to four questions per call; use rounds and batch related questions instead of a chain.

- Each question: a `header` of at most 12 characters, the full `question` ending with "?", and two to six
  `options` with a one-line `description` each. Put the option you recommend first.
- Set `multiSelect` when several options can apply, and bound it with `minSelect` / `maxSelect` when the count
  matters.
- Product round first. Only after those answers, the design round. Do not mix "what should it do" with "which
  class should own it" in one call.
- A question the user skipped is not an answer: decide, and write the decision into the task spec as an
  assumption with what would settle it.
- If the whole call was dismissed, continue on your best judgment, say which choices you assumed, and keep going.
- Parts of the design, the acceptance criteria or the edge cases the request already states are approved. Record
  them; do not ask them again. Never ask permission for work you were already asked to do.

## The task spec

Write the answers to `.forge/context/TASK-<slug>.md` (kebab-case slug from the work) and update it after every
round. Five sections, nothing else:

- `## Request`: what was asked, in one paragraph, plus the paths or issues it points at.
- `## Product`: the phase 1 answers, including empty, error and boundary behavior and the done criterion.
- `## Design`: the chosen design, the rejected alternatives in one line each, and the files or modules involved.
- `## Unconfirmed assumptions`: each assumption with what would settle it, and who owns it.
- `## Out of scope`: what this change deliberately does not do, so it is not smuggled in later.

It is scratch, not a deliverable. The step committer stages every uncommitted file when a step goes done, so keep
it out of commits: move its durable content into the owning spec (step 6) and delete it, and never let it reach
the pull request.

The design is accepted when the user approved it in an answer, or when the request itself already stated it. It is
not accepted because you are confident. Only after acceptance do you edit source files, plan the steps, or run
anything that writes to the worktree.

## A bug is the same workflow, shorter

- Reproduce first: a failing command, an existing test, a recorded trace. If it cannot be reproduced, say what is
  missing and ask for it; do not fix a guess.
- Find the root cause before the fix: read the code path, `grep` for the other callers, `git_log` and `git_diff`
  to see what changed. Write the cause in one sentence in `## Product`.
- Fix minimally: no opportunistic refactor in the same change. If a cleanup is needed, it is a separate group.
- Add or adjust a regression test that fails before the fix and passes after, in the project the repository uses
  for that layer. If the layer has no test, say so in the step's verification instead of pretending.
- The interview shrinks to one round: expected behavior, and who else is affected by the bug fix.

## Scale, not ceremony

- Small (a message, a default, a narrow condition): one short product round, a task spec of a few lines, one or
  two steps. Never zero.
- Medium (a new option, a changed flow): product round, design round, a plan of two to five steps.
- Large or cross-module: update the owning spec before the code as its own step, split the work by module, and
  consider a `Plan` sub-agent before starting.

Whatever the size, the sequence holds: product, design, accepted, planned, implemented, reviewed, spec updated,
task spec deleted, then `shipping-a-pr`.
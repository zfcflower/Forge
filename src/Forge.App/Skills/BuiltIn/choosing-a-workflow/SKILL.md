---
name: choosing-a-workflow
description: >-
  Use when a new piece of work arrives and it is not yet decided how it will be run: this routes it to exactly one
  workflow skill (brainstorming for any product behavior change, hardening-tests for work on the tests themselves,
  shipping-a-pr for delivering or tending a pull request) or to no workflow when nothing changes. Do not use it to carry the work out, to re-route work that is
  already inside a workflow, or for a question, a read-only look or an edit that changes no behavior.
---

# Choosing a workflow

This skill makes one decision and hands the work off. It reads no code, edits no file and writes no plan entry:
the workflow it names runs the rest.

## The decision

Decide once per piece of new work, by what the work changes rather than by how the request is worded.

| The work | Route |
|---|---|
| Product behavior: a feature, a behavior change, a bug, an output or UI difference, however small | `brainstorming` |
| The tests themselves: hardening a critical module's tests, pinning behavior through an important refactor, or auditing test quality on a schedule | `hardening-tests` |
| Handing finished work to review as a pull request, or tending a pull request that already exists (CI, review comments, a base that moved) | `shipping-a-pr` |
| Nothing user-visible changes: a question, an explanation, a read-only investigation, a typo, a prose-only doc edit, a rename that keeps every behavior | no route |

How to read that table:

- "Small" is not a route. A one-line condition, a default, a user-facing string, a validation rule and a log line
  the user reads are all product behavior.
- A design or an acceptance criterion the request already states does not move the work out of `brainstorming`;
  inside that workflow the stated parts are already approved.
- A rename, move or reformat is no route only while every observable behavior stays the same. If it also changes a
  message, a default, an ordering or a public shape, it is a behavior change.
- Verifying an important refactor is `hardening-tests`: running its tests against a contract and probing its
  boundaries. Running the suite once to see whether it is green, or locating a fault someone reported, is no route:
  nothing is being pinned down.
- One test someone suspects is hollow is a look, not a workflow. The sweep over a module's tests that follows is
  `hardening-tests`.
- A pull request's review, its tests included, is `shipping-a-pr` while the work is about that pull request.
- A refactor whose observable behavior stays the same is `hardening-tests` even though it edits source: pinning that
  behavior is the point. Only a refactor that changes something a user or a caller can observe is a behavior change.
- A defect someone already reports is a bug fix, so it goes to `brainstorming`; the failing reproduction test is that
  workflow's evidence, not a separate trip through this one.
- A recurring audit ("every Monday", "monthly") takes the same route as the one-off audit: `hardening-tests`.
- The router decides for the work as a whole, not for each step of it.

## How to route

1. Load the workflow by name with the `skill` tool: `skill {"name": "brainstorming"}`,
   `skill {"name": "hardening-tests"}` or `skill {"name": "shipping-a-pr"}`.
2. Say in one line which workflow you follow and why, for example: "This changes what the tool returns, so I start
   with brainstorming." No plan, no file edit and no interview before that line.
3. Follow the loaded skill. It starts with its own first step; come back here only when it hands off.

## No route: do it now

Do the work directly: read the file, answer the question, fix the typo, report what you saw. No task spec, no plan
entry, no review round. If the work turns out to change behavior while you are doing it, stop, say so, and route
it through `brainstorming`.

## What must not be re-routed

- Work already under a workflow stays under it. Do not re-route a step, a dropped requirement or a review finding
  to another workflow: a fix inside an implementation is still that implementation. The one hand-off that is not a
  second decision is the defect `hardening-tests` confirms: that skill ends and `brainstorming` owns the fix.
- `brainstorming` hands its finished work to `shipping-a-pr` itself, at the point that skill says. That hand-off
  belongs to the active route; it is not a second decision.
- When the user asks for the pull request of work you are already implementing, finish that implementation and take
  the `shipping-a-pr` route for the delivery only. Do not restart the interview.

## Ambiguous: ask one question

Ask through `ask_user` when the answer changes the route, never as a prose question and never as a chain:

- `header`: "Route"
- `question`: "Does this change what the product does for a user?"
- two or three `options`, the recommended one first, each with one line on what picking it implies ("Yes - start
  with brainstorming and confirm the behavior first" / "No - do it directly").

Anything you can settle by reading the code, the specs or the request is not ambiguity: look it up and route.

## Red flags

Each of these is a wrong route, not a shortcut:

- Editing product behavior - a condition, a user-facing string, a default, a validation - without `brainstorming`.
  "It is one line" and "the requirement is already clear" are the usual reasons and both are wrong: a one-line
  change gets a one-round interview, not zero.
- Skipping the interview because you already understand the design. Understanding buys fewer questions, not no
  record.
- Putting `brainstorming` on work that changes no behavior: the interview would ask the user about a change nobody
  is making. Answer the question or fix the typo instead.
- Re-routing work mid-flight, or running `brainstorming` and `shipping-a-pr` at the same time.
- Writing the plan, the task spec or a source edit before the route is chosen.
- Routing by the word the user used. "Refactor", "bug" and "feature" are labels; judge the change.
- Sending a behavior change to `hardening-tests` because it happens to touch tests, or a test audit to
  `brainstorming` because it will edit files. Who owns the change decides the route, not which files move.

## What each route starts with

- `brainstorming`: an interview in rounds through `ask_user`, a task spec under `.forge/context/`, then the plan
  in the `todo` tool, then implementation step by step with `request_review` on every code step.
- `hardening-tests`: the module's own tests, then the contract that governs it, then the repository's own test
  command, then the measurement and the probes. It never changes product behavior: a confirmed defect leaves for
  `brainstorming`, and finished tests leave for `shipping-a-pr`.
- `shipping-a-pr`: `pr_workflow` for the phase, `pr_create` after its gates, then CI and review comments driven
  back into the implementation. It never merges.
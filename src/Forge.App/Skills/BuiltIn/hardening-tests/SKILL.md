---
name: hardening-tests
workflow: true
description: >-
  Use after choosing-a-workflow routes work on the tests themselves here: hardening the tests of a critical module,
  pinning a module's behavior before or after an important refactor, or auditing test quality on a schedule. It finds
  what the suite does not check, confirms each gap, and writes the test that closes it. Do not use it for a feature, a
  behavior change or a bug fix (brainstorming owns those), for merely running the suite, or for delivering finished
  work as a pull request (shipping-a-pr).
---

# Hardening the tests

The tests are the subject; the product's behavior is not. Find what the suite does not check, show that it is missing,
and close it with a test that would fail if the code were wrong.

## What this workflow never does

- It does not change product behavior. A confirmed defect ends this workflow: report it and hand it to `brainstorming`.
  Writing the failing test is fine; fixing the code here is not.
- It does not add a test framework, runner, package, linter or coverage tool to the project. The command the repository
  already uses is the only one it runs.
- It does not report a coverage percentage or a mutation score. It reports the gaps that matter and the test each needs.
- It does not edit a source file inside the session's workspace. Every probe (step 3) is made in a separate worktree.
- It does not run the whole suite in a loop. Every run is bounded; see Budgets.

## 1. Settle the scope

The route in says which of the three the work is; the scope is what the rest of the workflow measures.

| Trigger | Scope |
|---|---|
| A critical module | the module named, plus the callers that shape its inputs |
| An important refactor | the members the diff touches, plus the siblings that must agree with them |
| A periodic audit | the three modules the repository changes most, weighted by how many modules call them |

Read the module's own tests first, then the specs, docs and doc comments that say what it must support. That set, not
what callers pass today, is the domain: a value no caller produces yet still belongs to it when the contract promises it.

Discover the test command once, from the repository rather than from memory: a CI workflow file, a Makefile or justfile
target, a package manifest's test script, or a manifest naming a test project. Run exactly that command, narrowed to
the affected project, class or file, and not a variant assembled by hand. When no command is discoverable, or the first
run would take longer than about ten minutes, stop and ask; do not install a toolchain to make one exist.

## 2. Find what the tests do not check

Two sources, cheapest first.

**Coverage the project already produces.** When the project can emit it without new tooling (a `--coverage` or
`--collect` flag, an lcov or cobertura report its CI already uploads), one bounded run names the lines and branches no
test reaches. When it cannot, skip this; do not add a collector.

**Reading.** For each member in scope, compare the code with what must agree with it: its sibling overloads and its
parallel paths, its doc comment, the arguments its real callers pass, its `git log -L` history, and any copy of it in a
test helper or a ported module. Read the member's own tests in the same pass: what they pin down, and which argument
every production caller passes that no test does.

Keep the gaps in a scratch list under `.forge/context/`, one line each: the member, the input or the check it lacks,
and the evidence. Nothing else yet: no score, no ranking.

## 3. Confirm a gap in isolation

A gap is confirmed when a small edit that compiles makes the tests pass when they should not. Make it in a worktree,
never in the session's workspace:

    git worktree add <scratch>/probe HEAD

Inside that worktree make one semantic edit at a time: flip a condition, drop a validation, change a default, remove an
early return, move a boundary. Keep it compiling, run only the affected tests, record killed or survived, and revert
before the next edit. Remove the worktree when the run is over.

A survivor alone does not confirm a gap: it can also mean the command never ran the tests that matter. So before
reporting any gap, show one probe that the suite does catch. When not one probe can be caught, report that the test run
is unverifiable and stop: the command, the filter or the project is wrong, and a list of survivors would be fiction.

A project that will not build, or a module whose tests cannot be run in isolation, is a reason to skip this step and say
so; coverage plus reading is then the whole measurement.

## 4. Close it

Write one test per group of gaps that the same test exposes, which is usually one input the tests lack and sometimes one
check made too loosely. Match the project: its framework, its assertion style, its fixtures, and its existing
property-testing library if it has one. Never add a package for this.

Take expected values from the specification, the documentation, a hand calculation or an independent implementation,
never from what the code returns now: a survivor says the tests cannot tell two versions apart, not which one is right.

Each code step goes through the plan and `request_review` like any other work; nothing is suspended because the change
is a test.

## 5. Stop

Exactly one of these ends the workflow.

- **Nothing worth closing.** Report the scope, what was measured, and why the rest is not a gap: no input the contract
  promises reaches it, it only logs, or it needs a seam the project does not have. No tests are written, which is a
  legitimate outcome.
- **Gaps closed.** Report each gap, its evidence and the test that now covers it, then hand the result to
  `shipping-a-pr` when the user wants it delivered.
- **A real defect.** Stop, report it with the probe that shows it, and hand it to `brainstorming`: the fix and its
  release belong to that workflow, not this one.

## Budgets

| Trigger | Probes | Test runs | Wall clock |
|---|---|---|---|
| A critical module | at most 5 | at most 8 | 30 minutes |
| An important refactor | at most 5 per side of the refactor | at most 8, with one baseline before | 30 minutes |
| A periodic audit | at most 3 per module, 3 modules | at most 10 | 60 minutes |

When a budget runs out, report the gaps found so far and what is left. Never widen the scope on your own.

## Red flags

- Turning a confirmed defect into a test that asserts the wrong output the code produces today.
- Reporting a gap list with no caught probe behind it.
- Editing the workspace to make a probe, or leaving a worktree behind.
- Installing a runner, a package or a coverage tool so that the measurement works.
- Writing tests in a module whose tests you have not read.
- Fixing the code because the gap was obvious. It goes to `brainstorming`, with the failing test as its evidence.

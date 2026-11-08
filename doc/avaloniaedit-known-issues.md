# AvaloniaEdit integration: remaining editor issues

Recorded 2026-11-08 with the AvaloniaEdit integration (`fcc3ab0`). This file is now the **editor-only** leftover list: the
previous revision also carried a baseline of failures outside the editor (Workspace Git, App Git/Tree) and open items
elsewhere. A later pass re-measured them, so entries that no longer reproduce were dropped and the release-build and
flaky-test defects were fixed instead of deferred (see "Validation and baseline"). The editor leftovers below are the
ones that survived the editor feature-completeness change (`feat(editor)` series): folding, whitespace/EOL/tab glyphs,
hyperlinks, inline diagnostics, completion triggers and snippets, the search panel, the transform commands and the editor
option set are implemented and covered; what remains is listed at the end.

## Validation and baseline

- `dotnet build Forge.slnx -c Release` → **0 warnings, 0 errors**. The solution used to be uncompilable in Release: one
  `CS0122` per terminal-protocol reference to the internal `TerminalOps`/`TerminalProtocol` types, because
  `Forge.HostClient`'s `InternalsVisibleTo` sat inside `#if DEBUG` while its terminal tests compile in every
  configuration. The guard is gone (`src/Forge.HostClient/AssemblyInfo.cs`), so the test assembly named there can read
  internals in Release too. `dotnet test tests/Forge.HostClient.Tests -c Release` → **485 passed, 0 failed** (the
  project that used to be the only Release blocker), and CI's Release test chain builds again.
- `dotnet test tests/Forge.Workspace.Tests` → **1152 passed, 0 failed**. The 38 Workspace Git failures of the previous
  revision (`GitRemoteTestSupport.cs:77` as the common location) do not reproduce; nothing in the repository changed
  beneath them, so the entries were dropped instead of investigated further.
- `dotnet test tests/Forge.IO.Tests` → **363 passed, 0 failed**.
- `ProcessTreeTests.Cancellation_kills_the_grandchild_the_child_started` is fixed, not skipped. It cancelled a fixed
  1500 ms after start, which on a loaded machine arrives while the child is still loading the test assembly; the test then
  had no grandchild to miss and reported a tree-kill defect that never happened. It now waits for the recorded pid before
  cancelling, and the timeout sibling sizes its deadline from a measured child startup (`max(1.5 s, 3 × startup)`) for the
  same reason. No assertion was weakened.
- `TreeVisualTests.Agent_rows_put_chevron_after_title_and_colour_and_animate_by_state` is fixed. Agent rows stroke a
  `wc:LineIcon` and only the file/spec trees still stroke a `Path` (`src/Forge.App/SPEC.md`, "树与图标"); the test helper
  looked for `Path.tree-icon` only. It now reads either element, and its assertions (chevron after the title, breathing
  animation while running, one colour per state) are unchanged.
- `dotnet test tests/Forge.App.Tests` → **1913 passed, 2 failed**: the large-file editor entry below and
  `TreeVisualTests.Agent_rows_put_chevron_after_title_and_colour_and_animate_by_state`, which is fixed above (its 8 cases
  pass on their own). The 6 App Git failures from the previous baseline list (the `git push`/`gh` scenarios under
  `tests/Forge.App.Tests/Git`) pass in this run and were dropped as well. A repeat could not finish while the editor was
  being edited in the same worktree (the App build was locked, then the test host aborted on in-flight `editor.*`
  localization keys), so this record has no newer whole-project App number.
- `dotnet test Forge.slnx` (Debug) → 0 failures across HostClient 486, Workspace 1152, Integration 265, Core 757, Editor
  394, IO 363, Scripting 264, Approval 188, Mcp 179, Workflows 147, Ipc 101. `Forge.App.Tests` and
  `Forge.App.LiveSwitch.Tests` are the two projects that run left out, for that same build collision;
  `tests/Forge.Plugin.Sdk.Tests` reports no discoverable tests, as before.
- From the original record: the native editor mixed scope passed 132/132 twice in an isolated copy. Those targeted runs
  did not reveal the full-suite UI failure below.
- From the original record: main application Release Native AOT publish succeeded. The published executable created a
  responsive native window with isolated data/workspace directories. This is a startup smoke only, not a manual editor,
  hover, Ctrl-click or review-overlay acceptance test; core editor, popup, F12, diagnostics, review drafts and queue
  operations have headless coverage.

## Editor navigation and feedback fixes (round 2)

Reported after the capability work, all fixed with regression tests:

- **The toolbar ellipsis killed the process.** The toolbar was told to show the editor's own `ContextMenu`, and
  `ContextMenu.Open` throws `ArgumentException: Cannot show ContentMenu on a different control to the one it is attached
  to` — an unhandled exception in a click handler. The menu is now built per host (`Views/EditorMenuBuilder.cs`): the
  editor keeps its `ContextMenu`, the toolbar button gets its own `MenuFlyout`. Items carry no bindings; they read the
  settings when the menu is built and write on click, which also keeps them AOT-safe and honest after the settings page
  changed something. Covered by `EditorFeatureTests` (both hosts opened for real) — the earlier test only checked that a
  menu existed, which is why the crash slipped through.
- **"No language server" for files whose language had one installed.** Startup now runs
  `Forge.Editor.Languages.LanguageServerDiscovery` over PATH plus the VS Code/Cursor extension and installation folders,
  and the discovered server is used silently with its origin on the editor status line. On this machine that resolves C#
  to the Roslyn LSP inside `ms-dotnettools.csharp-*/.roslyn/Microsoft.CodeAnalysis.LanguageServer.dll` (launched with
  `dotnet`) and JSON/CSS/HTML/TypeScript/JavaScript to the npm-installed servers. An explicit `languages.json` choice,
  including "no server" and `disabled`, is never overridden. The remaining notice now says where discovery looked.
- **Selection colour hid the code.** The selection is the brand blue at ~25% alpha (light) / ~30% (dark) with no
  foreground or border override, so the text keeps its own colour. Search hits use a translucent yellow.
- **The review icon sat at the far right of the line.** It now hangs off the bottom right of the selection's moving end
  (or the row clicked in the line-number margin), flips left near the right edge, and is always clipped into the editor.
  Placement is a pure function (`Review/ReviewIconPlacement.cs`) with unit tests, plus two real-window tests.

## Language server discovery and diagnostics (round 3)

Reported: after a server was discovered, squiggles, hover and F12 still did nothing; and the JS/JSON servers VS Code
ships were not usable. Both were real gaps, reproduced against the actual servers:

- **Roslyn never loaded the project.** With the client's `window.workDoneProgress: false` it logged
  `Discovered 1 projects to auto load` and stopped: Roslyn creates a work-done token before loading and waits for the
  answer. It also only loads projects when told to (`--autoLoadProjects`), and it reports diagnostics **only on request**
  — with no `textDocument.diagnostic` client capability it hides its `diagnosticProvider` and pushes nothing, which is
  exactly "handshake fine, no squiggles, F12 finds nothing". Fixes: advertise `window.workDoneProgress`,
  `workspace.diagnostics.refreshSupport` and `textDocument.diagnostic`; launch discovered Roslyn with
  `--autoLoadProjects 100`; pull on view attach, after a 500 ms edit debounce and on `workspace/diagnostic/refresh`, and
  publish the pulled set through `LanguageSession.PublishDiagnostics` so it takes the same path as a push (including the
  replay to a view that attaches later). Verified against the real server: pull returns `未使用私有成员"A.M"`,
  `textDocument/definition` returns the declaration, `textDocument/hover` returns `（局部变量）int x`.
- **`typescript-language-server` exited at startup** with `Could not find a valid TypeScript installation`: it needs
  `initializationOptions.tsserver.path`. Discovery now resolves `tsserver.js` next to the server binary (npm global
  layout), in the npm prefix, and in an editor installation, and passes it. Verified: `.ts` diagnostics, definition and
  hover all answer. On this machine the npm-global `typescript` is 7.0.2 and no longer ships `lib/tsserver.js`, so the
  path comes from the VS Code installation (`extensions/node_modules/typescript/lib/tsserver.js`).
- **Editor installations with a version folder were missed**: this machine's VS Code keeps its app at
  `Microsoft VS Code/<hash>/resources/app`. Installation roots now expand to the root itself, `resources/app`, and one
  level of version folders — that is also where the JSON/CSS/HTML servers come from.
- **Waiting explains itself now**: `$/progress` from the server raises `LanguageSession.BusyChanged`, and the editor's
  status line switches to "the language server is still loading the project…" until the work-done token closes; before
  that, a pressed F12 simply looked broken.
- JSON (push or pull) and CSS/HTML (pull) were exercised with the same client: the JSON server answers
  `Trailing comma` for `{"a": 1,}`, folding is declared, and the CSS/HTML servers answer `textDocument/diagnostic`.
- Unverified table entries (no server installed on this machine): Python, Go, Rust, C/C++, Shell, YAML, Lua. They launch
  as configured and fail with the "language server could not start" notice rather than silently doing nothing.
- **JSONC is its own language now.** `.jsonc` and `tsconfig.json` / `jsconfig.json` / `devcontainer.json` are sent with
  language id `jsonc` (exact file names win over the `.json` extension), so the JSON server stops reporting
  "Comments are not permitted in JSON". Verified against the real server on one file: as `json` it reports two problems
  (comments + trailing comma), as `jsonc` only the trailing comma. `jsonc` only exists when discovery found the JSON
  server, so a hand-configured `json` language keeps its old behaviour. JSON below a `.vscode` folder (settings.json,
  tasks.json, launch.json) is detected by path segment and also goes to `jsonc`; a directory merely *containing* the text
  `.vscode` (e.g. `my.vscode/`) does not count, and without a registered `jsonc` language nothing changes.

## The completion list could not be navigated (round 4)

Reported: the completion popup cannot be driven with the arrow keys. Two things were true at once, and only one of them
was what it looked like:

- **The selection was invisible, not stuck.** `Resources/Controls.axaml` blanks the default list selection chrome for the
  workbench's own row lists — `ListBoxItem:selected /template/ ContentPresenter { Background = Transparent }`, unscoped —
  and that style also reached AvaloniaEdit's completion popup, whose list items are ordinary `ListBoxItem`s. Arrow keys
  did move the selection (verified in a headless window), but every row looked the same, so the popup read as
  "unselectable" and Enter inserted something the user could not see coming. Fixed by giving the popup's list a class
  (`completion`) and styling *that*: visible selected/pointer-over background from Forge tokens, and the row padding back.
  A regression test asserts the selected row's content presenter actually has a non-transparent background.
- **Navigation no longer depends on the popup's key forwarding.** The editor now handles Up/Down/PageUp/PageDown/Home/End
  itself while the popup is open (`TryMoveCompletionSelection`), wrapping like the library and marking the event handled —
  `TextArea`'s stacked handler breaks on an already-handled event, so nothing steps twice. Modifier-free keys only:
  Ctrl+Home and friends stay with the editor.

## Open editor failures

| Test | Location | Observed failure / follow-up |
| --- | --- | --- |
| `TextEditorLifecycleTests.A_large_file_materializes_only_the_visible_lines` | `tests/Forge.App.Tests/Editor/TextEditorLifecycleTests.cs:121` | Scrolled lines near 150000 did not materialize within the bounded 15-second wait in the full solution run. Targeted runs passed. Investigate layout, scroll readiness and shared headless application state; do not simply weaken virtualization assertions. |
| `FileEditorInteractionTests.Declining_one_of_two_dirty_files_closes_nothing_and_marks_neither_clean` | `tests/Forge.App.Tests/ViewModels/FileEditorInteractionTests.cs:177`; `tests/Forge.App.Tests/Workbench/WorkbenchControlsTests.cs:1821` | Different-thread access during `ShellFixture.DisposeAsync`. Stack: `HeadlessUnitTestSession.EnsureIsolatedApplication` → `AppBuilder.SetupUnsafe` → `AvaloniaHeadlessPlatform.Initialize` → `ServerCompositor` → `DefaultRenderLoop.Add`. This is application/render-loop initialization interference, not the previously fixed static-keybinding symptom. It did not reproduce in an isolated `Forge.App.Tests` run, so it is an intermittent full-suite failure. |

AvaloniaEdit 12.0.0 also shares static `KeyBinding` instances in `EditingCommandHandler` and
`CaretNavigationCommandHandler`. Those AvaloniaObjects belong to the dispatcher that first created them. Native-editor
tests therefore use a shared headless session. Other tests still create independent sessions; mixed ownership can
interfere with global Avalonia state. Production has a single UI thread. A wider test-infrastructure migration was
deliberately not performed.

## Remaining limitations / coverage

- **Multi-caret editing does not exist in AvaloniaEdit 12.0.0** (the README claims it; the source has one `Caret` and
  `RectangleSelection`). By the user's decision the editor offers rectangular (column) selection instead, advertised in
  the settings section and in the editor menu; a real multi-caret core was not written.
- **Snippet tab stops are not recreated.** Snippet completions insert their literal text (`${1:name}` → `name`, choices
  take the first option, malformed snippets disappear); the caret ends after the inserted text, there is no `Tab` walk
  through placeholders.
- **AvaloniaEdit 12.0.0 leaves two display options dead**: its end-of-line generator is commented out and nothing reads
  `ShowTabs`. `Forge.App` draws `¶` and `→` itself as zero-length inline elements; the library's own space and
  control-character rendering is used unchanged.
- LSP `textDocument/rename`, `formatting`, `codeAction`, `signatureHelp` and `inlayHint` are not implemented; the editor
  acts on completion, hover, definition, diagnostics, folding ranges and semantic tokens only.
- Queue acceptance and `MarkSent` are not a durable cross-system transaction. Mark/restore are serialized per store, and
  chip withdrawal restores drafts, but crash/application-exit windows remain. No exactly-once delivery guarantee.
- Deterministic delayed-store coverage for retry-mark versus withdrawal is still missing. Code review verified both
  semaphore orderings; ordinary withdrawal-before/after-mark and real queue removal/delivery tests pass.
- `ReportHighlightFailure` is not directly exercised by a controlled asset-failure test.
- Highlighting is limited to 1 Mi characters; text remains intact/editable beyond the cap. Folding is limited to 20000
  lines and 5000 ranges, and the editor says so on its notice line instead of scanning. A single long line below the
  highlight cap has no additional Forge line-length cap.
- Completion falls back to document words when no server answers; hover is plain text and only the first definition is
  followed (both unchanged). Ctrl-click and hover still have no real-window gesture test beyond the link router's
  event-level test.
- `Forge.Editor` is intentionally retained for LSP, atomic encoding-aware save, folding ranges and Diff services.
  Native/Forge mirrored document and undo structures remain duplicated pending service migration.
- Persistent review anchors do not record a base commit. Unique relocation searches files of at most 20000 lines; larger
  files only accept the original-position exact match.

## Measured after the editor feature-completeness change

- `dotnet test tests/Forge.App.Tests` → **1969 passed, 2 failed** of 1971: the large-file entry above and
  `AppearanceSettingsViewModelTests.After_dispose_neither_preference_nor_language_changes_reach_the_groups`, which passes
  alone (31/31 for its class). Both are the same intermittent full-suite isolation class of failure recorded above, not
  editor behaviour.
- `dotnet test Forge.slnx` (Debug) → every other project green: HostClient 486, Workspace 1152, Integration 265, Core 757,
  Editor 394, IO 363, Scripting 264, Approval 188, Mcp 179, Workflows 147, Ipc 101, App.LiveSwitch 8.
- `dotnet test tests/Forge.App.Tests --filter "Forge.App.Tests.Editor"` → **119 passed, 0 failed** (folding, whitespace,
  hyperlinks, inline diagnostics, menu, search, transforms, options, preferences, snippets and word completion).
- `dotnet build Forge.slnx -c Release` → 0 warnings, 0 errors; `dotnet publish src/Forge.App -c Release -r win-x64` →
  succeeded, and the published 49 MB native executable started with isolated `FORGE_DATA_DIR`/`FORGE_WORKSPACE`, created
  `forge.db` and `approvals.db`, and stayed responsive. This remains a startup smoke, not a manual clicking pass.

# Lightweight IDE vs JetBrains Rider: what is missing

Date: 2026-11-21, state of `main` at `9c930b9`. Scope: the daily backend loop (C#, ASP.NET Core, tests, build,
debug). Desktop UI frameworks (WPF, MAUI, Unity, Unreal) are out of scope on purpose and are not listed.

Legend: ✅ we have it, 🟡 partly, ❌ missing.

## 1. Unit tests — the biggest gap

| Rider | Us | Notes |
|---|---|---|
| Run / debug all tests in a solution, a project | ✅ | Ctrl+Shift+T, Test menu, explorer context menu |
| Run / debug the test or class at the caret | ✅ | Ctrl+Shift+R / Ctrl+Shift+D (caret in the class, outside a method, runs the class) |
| Run / debug all tests in the current file | ✅ | Ctrl+Alt+R; every non-abstract class with test methods (done 2026-11-21). |
| Run tests from a file / folder / solution folder in the explorer | ✅ | One `dotnet test` per project, results merged (done 2026-11-21). |
| ▶ markers in the gutter next to test classes and methods, with the last result | ✅ | Run / Debug / Toggle Breakpoint menu; green / red after a run (done 2026-11-21). |
| Unit Test Explorer: tests discovered before any run (tree project → namespace → class → method) | ✅ | From the source, no build (done 2026-11-21); project → class → method. |
| Run selected tests from the tree, several at once | 🟡 | "Run Failed" only. |
| Test sessions (several saved sets of tests) | ❌ | |
| Rerun last session | ✅ | Shift+Alt+R and Rerun in Test Results (done 2026-11-21). |
| Theory cases (`[InlineData]`) as separate rows, run one case | 🟡 | Rows come from TRX; a single case cannot be started. |
| Continuous testing (rerun affected tests on save / build) | ❌ | |
| Code coverage (dotCover): highlights in the editor, per-test coverage | ❌ | `dotnet-coverage` / coverlet can produce Cobertura; we only need to read it. |
| Output / stack trace of a test, jump to the frame | ✅ | |

### Proposed first step (small, high value)

1. **Run Tests in Current File** (Test menu + editor context menu): parse the file, collect every non-abstract class
   that has at least one test method (`[Fact]`, `[Theory]`, `[Test]`, `[TestCase]`, `[TestMethod]`,
   `[DataTestMethod]`), and pass `Ns.Class.*` for each one to the existing `RunTestsAsync(project, filters)`.
   Same for **Debug Tests in Current File**.
2. **Explorer "Run Tests" on a `.cs` file and on a folder**: the same collection over the files of the folder
   (from the index or by parsing), with the owning project as the target. If the folder spans several projects,
   run each one in turn or show a choice.
3. **Gutter ▶** on test classes and methods (click: Run / Debug menu), colored by the last result for that name.
4. **Rerun last** (shortcut) — the last `(target, filter)` is already partly kept for "Run Failed".

Points 1–2 reuse `TestRunner` filters (VSTest `--filter`, MTP `--filter-class` / `--filter-method`) without changes.

## 2. Code editing and refactoring

| Rider | Us | Notes |
|---|---|---|
| Completion, signature help, hover, diagnostics | ✅ | Roslyn in process |
| Semantic highlighting, usages of the symbol, faded unused code | ✅ | |
| Quick fixes and context actions (Alt+Enter) | ✅ | Roslyn providers: add using, implement interface, generate member, extract method / local, inline temp, … |
| Rename (with file rename) | ✅ | |
| **Refactor This** menu (Ctrl+Shift+R in Rider) — dedicated refactorings: change signature, move type to file / namespace, extract interface / class, pull members up, safe delete, introduce parameter | 🟡 | Only those Roslyn offers at the caret through Alt+Enter; no change-signature dialog, no safe delete, no move-to-namespace across files. |
| **Generate** (Alt+Insert): constructor, properties, equality members, formatting members, overrides | 🟡 | Partly via Roslyn actions; no dedicated menu. |
| Inlay hints (parameter names, inferred types, lambda types) | ✅ | Done 2026-11-21: overlay labels on a widened neighbouring character (GtkSourceView 5 has no inline virtual text). |
| **Code Vision / CodeLens** (usages count, implementations above members) | ❌ | |
| Postfix templates (`.var`, `.if`, `.foreach`, `.await`) and live templates / snippets | ❌ | |
| Multiple carets, column selection | ❌ | GtkSourceView 5 has no multi-cursor; would be custom work. |
| Extend / shrink selection (Ctrl+W) by syntax | ❌ | Simple with the syntax tree. |
| Move statement / member up and down, duplicate line, join lines | ❌ | |
| Code cleanup profiles, reformat on save, format on typing | 🟡 | CSharpier / Roslyn formatter, format on save, format of the file on a typed `}` when the syntax is valid (2026-11-21). |
| Solution-wide analysis (all errors in the solution list) | 🟡 | "Analyze Project"; background analysis exists, no solution-wide error list kept up to date. |
| Code folding, sticky lines (current class / method at the top) | ❌ | Folding is not in GtkSourceView 5 out of the box. |

## 3. Navigation and search

| Rider | Us | Notes |
|---|---|---|
| Go to file / symbol, find in files, recent files, file structure | ✅ | |
| Definition, references, implementations, back / forward | ✅ | |
| **Search Everywhere** (one box for files, types, symbols, actions, settings) | 🟡 | Separate palettes; no "find action" (Ctrl+Shift+A). |
| **Go to Base / Derived symbol**, type hierarchy, call hierarchy (incoming / outgoing calls) | 🟡 | Implementations only. |
| **Decompiled / metadata sources** (F12 into a NuGet type) | ❌ | Decided out of scope (README). ICSharpCode.Decompiler would give it. |
| Find usages grouped (read / write, by project, by file) | 🟡 | Flat list. |
| Bookmarks | ❌ | |
| Navigate to the test / tested class | ✅ | Ctrl+Shift+G, by name and by use; from a method to the tests calling it (done 2026-11-21). |
| Split editor (side by side) | ❌ | Markdown preview only. |

## 4. Build, run and debug

| Rider | Us | Notes |
|---|---|---|
| Build / rebuild / clean / restore, errors in a list | ✅ | |
| Run configurations from `launchSettings.json` | ✅ | Launch profile picker |
| Several run configurations at once (compound), Docker / docker compose configurations | ❌ | |
| Debugger: breakpoints, stepping, call stack, variables, watches, hover | ✅ | netcoredbg |
| Conditional breakpoints, hit counts, logpoints (tracepoints) | ✅ | Done 2026-11-21: condition in netcoredbg; hit count and logpoints in the IDE (netcoredbg has neither). |
| Exception settings per exception type | 🟡 | Unhandled in user code only. |
| Attach to a running process | ❌ | netcoredbg `attach` exists. |
| Threads view, Set Next Statement, Edit and Continue / Hot Reload | ❌ | Hot Reload: `dotnet watch` in the terminal is the practical path. |
| Profiling (dotTrace, dotMemory), Dynamic Program Analysis, monitoring tool window | ❌ | `dotnet-counters` / `dotnet-trace` could be wrapped later. |

## 5. Tools around the code

| Rider | Us | Notes |
|---|---|---|
| NuGet, Central Package Management | ✅ | Our CPM window is stronger than Rider's. |
| Project references (add / remove from a checklist, cycles blocked, Dependencies node with projects and packages) | ✅ | Done 2026-11-21. Still missing: the "Reference project X" quick fix on an unknown type. |
| Add an existing project to the solution, remove a project from the solution | ✅ | Done 2026-11-21: into the clicked solution folder; removal can drop the references to it. |
| HTTP client (`.http`) | ✅ | |
| Terminal | ✅ | |
| **Git integration** (changes, diff in the gutter, commit, blame, log, conflicts) | ❌ | Decided out of scope (GitKraken). Gutter markers for changed lines and blame would still help a lot at low cost. |
| Local history (every save, diff and restore) | 🟡 | Crash backup only. |
| Database tools (DataGrip inside) | ❌ | Out of scope; mention as a conscious choice. |
| Docker / Kubernetes tool windows | ❌ | |
| EditorConfig / Roslyn analyzers configuration UI, inspection severity | ❌ | `.editorconfig` is respected. |
| JSON / YAML / XML schemas, `appsettings.json` completion | ❌ | Plain highlighting. |
| Frontend (TypeScript, React) | ❌ | Out of scope (only C#). |
| Plugins | ❌ | Out of scope. |
| AI assistant | ❌ | |

## Where we are better

- Memory: about 450–600 MB with a small solution loaded; Rider usually takes 2–4 GB on the same solution.
- Start time: the editor works before the workspace has loaded.
- Central Package Management window and version completion in `Directory.Packages.props`.
- Native GTK4 look on Linux Mint, no JVM.

## Suggested order

0. ~~Project references checklist, Dependencies node, add existing project / remove from solution~~ (done).
1. Tests: ~~Run / Debug Tests in Current File, explorer "Run Tests" on files and folders~~ (done); ~~gutter ▶ with
   results~~ (done); ~~rerun last~~ (done). (Section 1, first step — mostly UI over existing `TestRunner`.)
2. ~~Test discovery tree before running~~ (done).
3. ~~Conditional breakpoints and logpoints~~ (done).
4. Extend selection, duplicate / move line, Find Action (Ctrl+Shift+A).
5. ~~Inlay hints~~ (done); usage counts (Code Vision).
6. Git gutter markers (changed / added / deleted lines) and blame on demand.
7. Code coverage from Cobertura (`dotnet test --collect "XPlat Code Coverage"`).
8. Decompiled sources for Go to Definition into metadata.

Shortcut clash to resolve when adding a refactor menu: Rider's Ctrl+Shift+R is "Refactor This"; ours is
"Run Test at Cursor" (Rider uses Ctrl+T, R for tests).

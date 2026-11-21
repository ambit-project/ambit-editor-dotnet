# Lightweight IDE

![Status: proof of concept](https://img.shields.io/badge/status-proof%20of%20concept-orange)
![License: MIT](https://img.shields.io/badge/license-MIT-blue)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![GTK4](https://img.shields.io/badge/GTK-4-4A86CF)

> **Proof of concept.** This project checks whether a native, lightweight editor built on GTK4 and Roslyn can cover
> the everyday work on large .NET backend solutions on Linux. It is used daily by its author, but it is not a
> finished product: expect rough edges, missing features and breaking changes. No support or stability is promised.

A native C# / .NET editor for Linux Mint built with GTK4 (GirCore), GtkSourceView 5 and Roslyn. It covers the
daily loop on large backend solutions (open, navigate, edit, build, test, terminal) and uses far less memory than a
full IDE: about 450–600 MB with the Roslyn workspace of a small solution loaded.

## Features

- **Editor:** tabs, C# highlighting, line numbers, current line, bracket matching, auto-indent, find and replace
  (regex, whole word, case), go to line, toggle comment, encoding and BOM detection (UTF-8 by default, line endings
  preserved), detection of changes on disk with reload, protection of unsaved changes.
- **Code intelligence (Roslyn, MSBuildWorkspace):** live diagnostics, completion, hover (a popup the pointer can move
  onto: Go to definition, Implementations, and links to the related types — a variable's or member's type, a method's
  return / parameter types, a class's base and interfaces), Go to Definition
  (F12 or Ctrl+Click), Find All References (Shift+F12), Roslyn diagnostics for a whole project, solution-wide
  **Rename** (F2 / Shift+F6) with a preview; a type rename can rename its file too.
- **Alt+Enter (Ctrl+.):** Roslyn's quick fixes for the diagnostics on the line (add using first, implement
  interface, generate type / member, …) and refactorings at the caret; changes apply as one undo step.
- **Go to Tests / Tested Code (Ctrl+Shift+G):** from a class, its test classes (named after it: `OrderServiceTests`,
  `…IntegrationTests`, `…Should`) and the test classes that use it; from a method, the test methods that call it; from
  a test, the tested class (by name without the suffix, otherwise the type the test uses most). One result opens,
  several go to Find Results.
- **Recent files (Ctrl+E)**, **file structure (Ctrl+F12)**, **Go to Implementation (Ctrl+Alt+B)**: implementations
  of interfaces and their members, derived classes, overrides.
- **Crash safety:** unsaved buffers are copied every 5 s to `~/.local/state/lightweight-ide/backup`; after a crash or
  a hard reset the IDE offers them back when the folder opens.
- **Navigation history:** Back / Forward (Alt+Left / Alt+Right, mouse buttons 4/5) over every jump.
- **Formatting (Ctrl+Shift+I):** CSharpier when the repository pins it in `.config/dotnet-tools.json` (or it is
  installed globally), through a warm `csharpier server`; otherwise Roslyn's formatter with `.editorconfig`.
  Only changed parts are replaced, so the caret stays and Ctrl+Z undoes it. Toolbar button and explorer
  "Format File" (a file without unsaved changes is saved after formatting). **Format on type:** a typed `}` that
  closes a block formats the C# file, only when the file has no syntax errors (`Formatting:FormatOnType`, on by
  default). Optional format on save.
- **Central Package Management:** the NuGet window's Central tab lists every PackageVersion of
  `Directory.Packages.props` with the projects using it (and VersionOverride), checks the feeds for updates for the
  whole solution, updates one or all versions in place, removes unused entries, and converts a solution to CPM
  (one version per package, the highest when projects disagree). "Add…" puts a package into several projects at
  once. In `Directory.Packages.props` / `.csproj`, typing in `Include="…"` suggests packages (most downloaded first)
  and in `Version="…"` that package's versions (newest first).
- **NuGet (Ctrl+Alt+N, or the project's context menu):** installed packages with available updates, update one or
  all, remove, search and add (optionally a chosen version). Uses `dotnet package …`, so NuGet.config sources and
  Central Package Management (`Directory.Packages.props`) work as on the command line.
- **Project references:** Project References… (the project's context menu or Build menu) lists the solution's other
  projects with checkboxes; checking or unchecking adds or removes `<ProjectReference>` through `dotnet add / remove
  reference`, then the Roslyn workspace reloads. Projects that already depend on this one are greyed out (a reference
  would create a cycle). Each project has a **Dependencies** node in the explorer: Projects (Enter reveals the
  project, Remove Reference in the context menu) and Packages with their versions (from the project or from
  `Directory.Packages.props`; Enter opens the NuGet window).
- **Add Existing Project…** (File menu, the solution's or a solution folder's context menu): pick one or more
  `.csproj` files; `dotnet sln add` puts them into the clicked solution folder. **Remove from Solution** on a
  project: `dotnet sln remove`, the files stay on disk; when other projects reference it, the dialog names them and
  can remove those references too.
- **Solution explorer:** `.sln` and `.slnx`, solution folders, projects, lazy tree, new, rename and delete
  (to the trash). Two tabs: **Solution** and **Files** (the folder on disk, listed when the tab is first opened). `bin`, `obj`, `.git`, `.vs` and `node_modules` are hidden.
- **Index (SQLite FTS5)** kept per folder in `~/.cache`: Quick Open (Ctrl+P), Go to Symbol (Ctrl+Shift+P),
  Find in Files (Ctrl+Shift+F). Incremental (mtime + size), updated by the file watcher, rebuilt on demand.
- **Build:** build, rebuild, clean and restore for the solution or the current project, Debug or Release, with
  errors parsed into Problems. **Tests:** `dotnet test` in VSTest or Microsoft.Testing.Platform mode with TRX
  results, run all, run a project, ▶ in the gutter next to every test class and method (Run / Debug / Toggle
  Breakpoint; green or red after a run), **Rerun** the last run (Shift+Alt+R), run the test at the cursor (Ctrl+Shift+R) or every test class of the current file
  (Ctrl+Alt+R). In the explorer, Run Tests also works on a `.cs` file, a folder (the test classes found in its files,
  one `dotnet test` per project) and a solution folder (its test projects).
- **.http files** (REST Client / Visual Studio format): ▶ in the gutter or Ctrl+Enter sends a request; file variables,
  `# @name` requests and `{{login.response.body.$.token}}` (a referenced request is sent first when needed),
  `$guid` / `$randomInt` / `$timestamp` / `$datetime` / `$processEnv` / `$dotenv`, `http-client.env.json`; cookies
  kept per file; responses in the HTTP panel (formatted JSON, headers, the sent request, history).
- **Launch profiles:** Debug → Launch Profile… picks a `launchSettings.json` profile for F5 and Run; `launchBrowser`
  opens the URL when the app starts listening.
- **Test explorer** (Test Results panel): the tests of every test project found in the source before the first run
  (project → class → method, theory cases under their method), with the latest result of each test merged in; ▶ Run /
  Debug the selected node, Failed Only, double click opens the source.
- **Inlay hints** (View → Inlay Hints, Ctrl+Alt+H; `Editor:InlayHints`): parameter names before literal arguments
  and inferred types after `var`, `foreach (var …)`, `out var`, deconstruction and implicitly typed lambda parameters.
  Drawn as overlays on a widened neighbouring character, so the text, offsets, undo and saving never contain them.
- **Tests:** results grouped by class, Run Failed, clickable stack frames, Debug Test at Cursor (Ctrl+Shift+D) and
  Debug Tests in Current File —
  VSTest by attaching to the waiting test host (`VSTEST_HOST_DEBUG`), Microsoft.Testing.Platform by launching the
  test app with `--filter-method`.
- **Terminal:** VTE shell in the opened folder. `dotnet run` and common dotnet commands start from the menu.
- **New solution (Ctrl+Shift+N):** pick an installed SDK (written to `global.json` first, so the solution and its
  projects are created by that SDK's templates), roll-forward policy, target framework (as offered by that SDK),
  `.slnx`, a first web/backend project and an xUnit test project, `Directory.Build.props`, Central Package
  Management (template versions are moved to `Directory.Packages.props`), `.editorconfig`, CSharpier, `git init`.
- **Add Project** (File menu or the solution's context menu): Web API, Empty, Blazor, gRPC, Worker, Class Library,
  xUnit / MSTest / NUnit, under `src/` or `tests/`, added to the solution with an optional project reference.
- **SDK and global.json:** change the SDK, roll-forward policy and `dotnet test` runner of the opened folder; the
  workspace reloads with the new SDK.
- **Debugger (netcoredbg):** F5 builds and debugs the startup project, Ctrl+F5 runs it in the terminal
  (no file has to be open). The project type is read from the `.csproj` — web, worker, console, test or library,
  with its own icon in the explorer; the startup project is the one set with **Set as Startup Project** (explorer),
  else the current file's runnable project, else the only runnable one, else a choice of the runnable ones (test
  projects left out). A project's context menu also has **Run** and **Debug**. The toolbar shows the startup
  project and its launch profile ("Ksef.Api · http"); its menu switches the project, picks a profile from
  `Properties/launchSettings.json` (Run passes `--launch-profile`) and opens that file; launch profile environment and URL from `launchSettings.json`), breakpoints in the gutter or F9 (Ctrl+Shift+F9 removes all of them, Debug menu: only the current file's);
  **conditional breakpoints and logpoints** (right click in the gutter or Ctrl+Shift+F8): a C# condition evaluated
  by netcoredbg, a hit count rule (equal to / at least / multiple of N) and a log message with `{expressions}` that
  is written to Output instead of pausing (both applied by the IDE: netcoredbg supports neither)
  (kept between sessions, moved with edits), F10 / F11 / Shift+F11 stepping, Shift+F5 stop, call stack, expandable
  variables, watch expressions, values on hover while paused, break on unhandled exceptions in user code, program
  output in the Output panel. Works with .NET 10 and .NET 11 programs.
- **Markdown preview** for `.md` files (Ctrl+Shift+V: side by side / preview / source): native rendering of
  headings, emphasis, code, lists, task lists, quotes, tables, local images (also `<img>` in README headers) and
  links (files open in the IDE, `#anchors` scroll, web links open in the browser); the preview follows the source
  scroll position.
- **Version** in the status bar (right), the window title and Help → About (with the commit); a local build shows
  `dev`. `packaging/build-deb.sh <version>` sets it.
- Light, dark and system themes. Every command has a keyboard shortcut (Help → Keyboard Shortcuts, Ctrl+F1).

Every opened folder uses the SDK selected by its own `global.json` (.NET 10 and .NET 11 / C# 15 side by side).

## Requirements

```bash
sudo apt install libgtk-4-1 libgtksourceview-5-0 libvte-2.91-gtk4-0
```

You also need the .NET SDK 10 or newer. For debugging, extract `netcoredbg-linux-amd64.tar.gz` from
https://github.com/Samsung/netcoredbg/releases (MIT) to `~/.local/share/lightweight-ide/` (or put `netcoredbg` on
`PATH`). Microsoft's vsdbg cannot be used: its license allows it only in Visual Studio and VS Code. Without VTE the IDE still works, only the terminal panel is disabled.

## Install from a release

```bash
sudo apt install ./lightweight-ide_<version>_amd64.deb   # from the GitHub release assets
```

The package is self-contained (its own .NET runtime, installed to `/opt/lightweight-ide`); building projects still
uses the .NET SDK on `PATH`. Build a package with `packaging/build-deb.sh [version]` (output in `dist/`).

## Run

```bash
dotnet run --project src/LightweightIDE.App -- /path/to/folder-or-solution-or-file
scripts/install.sh      # publish to ~/.local/share/lightweight-ide/app plus launcher and desktop entry
```

## Settings and files

| What | Where |
|---|---|
| Defaults | `src/LightweightIDE.App/appsettings.json` |
| User settings (File → Settings, read at startup) | `~/.config/lightweight-ide/settings.json` |
| Window state, recent folders, open files | `~/.local/state/lightweight-ide/state.json` |
| Logs (Serilog, 7 days) | `~/.local/state/lightweight-ide/logs/` |
| Index database per folder | `~/.cache/lightweight-ide/` |

Sections: `Ui` (theme, memory usage in the status bar), `Editor` (tab size, spaces, font, wrapping, threshold for
large files), `Language` (load the workspace on open, diagnostics delay, configuration, reference limit),
`Indexing` (maximum file size for full-text search, watcher debounce), `Formatting` (`Engine`: `Auto`, `CSharpier`
or `Roslyn`; `FormatOnSave`).

## Architecture

| Project | Responsibility |
|---|---|
| `LightweightIDE.Editor` | Document model: encoding detection, save, disk change detection |
| `LightweightIDE.DotNet` | Solution discovery (`.sln`/`.slnx`), file tree, project lookup, build and test runners, git branch |
| `LightweightIDE.Indexing` | SQLite FTS5 index, symbol extraction (Roslyn syntax only), watcher, fuzzy search |
| `LightweightIDE.Language` | MSBuildWorkspace, diagnostics, navigation, completion |
| `LightweightIDE.App` | GTK4 UI: window, explorer, editor tabs, bottom panel, palette, VTE terminal |
| `tests/LightweightIDE.Tests` | xUnit v3 tests, including loading real `net10.0` and `net11.0` projects |

Rules that keep it light: Roslyn and indexing run off the GTK thread and report back through the main loop; every
long operation can be cancelled; the explorer lists one directory level at a time; the index stores symbols from
syntax trees, so it needs no compilation; the Roslyn workspace loads in the background and the editor is usable
before it is ready.

## Scope and assumptions

The specification this was built from ends in section 9 (build). The rest was decided as follows:

- Known netcoredbg limitation: a property named `Length` cannot be evaluated on arrays (strings work through a
  workaround); expand the variable instead.
- No plugins, no languages other than C# (other text files open with plain highlighting), no WinForms, WPF or MAUI.
- Git is left to a dedicated client (e.g. GitKraken); the status bar only shows the branch.
- Navigation into metadata (decompiled sources) is not supported. Go to Definition reports the assembly instead.
- `Microsoft.Build.Locator` is not used: the IDE does not load MSBuild into its own process for builds (it calls
  the `dotnet` CLI), and MSBuildWorkspace starts its own build host.

## Tests

```bash
dotnet test --project tests/LightweightIDE.Tests
```

## Status

Proof of concept, developed in the open. Issues and ideas are welcome; there is no release schedule.

## License

[MIT](LICENSE) © 2026 Krzysztof Łopuch. netcoredbg, used for debugging, is a separate download under its own MIT
license.

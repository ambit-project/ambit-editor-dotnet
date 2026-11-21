# Roslyn Language Server (LSP): assessment and Milestone 1

Date: 2026-11-20. Status: Milestone 1 (technical validation) done; migration not started.

## 1. Current architecture (what the IDE does today)

- One process. `LightweightIDE.Language` hosts an in-process `MSBuildWorkspace` (Roslyn 5.12 packages) and builds
  every feature on it: completion (`CompletionService` + own ranking and smart types), signature help (own, on the
  semantic model), hover / QuickInfo, diagnostics, go to definition / implementation, find references, rename
  (`Renamer`), code fixes and refactorings (providers loaded by reflection), formatting (Roslyn or CSharpier),
  and the solution analysis index (`CodeModel`).
- `LightweightIDE.Indexing` (SQLite FTS5) does file / text / symbol search without Roslyn.
- The editor (`EditorTab`, GtkSourceView 5) calls the services off the UI thread (`Task.Run`) and posts results back
  with `Ui.Post`; unsaved text reaches Roslyn as overlays (`WorkspaceService.UpdateText`).

### Editor and GTK integration

- Offsets: GTK works in characters (code points), Roslyn in UTF-16; `TextOffsets` converts. LSP positions
  (line + UTF-16 column) would need one more converter, tested on Polish letters, emoji, CRLF.
- Popups (completion, signature, hover, code actions) are native GTK popovers, independent of the data source.
  They can show LSP results with a mapping layer.

## 2. Official server: compatibility report (verified, not taken from the docs)

| Question | Finding |
|---|---|
| Package | `roslyn-language-server` on nuget.org, a .NET tool (`DotnetToolSettings` v2) with RID packages (`roslyn-language-server.linux-x64`, arm64, musl). |
| Versions | Prerelease only: 12 versions, newest `5.13.0-1.26514.10` (built from dotnet/roslyn `d82b7f7`). No stable release. |
| Licence | MIT (`requireLicenseAcceptance`); redistribution in our .deb is allowed with the notices (ThirdPartyNotices.rtf). |
| Runtime | `net10.0`, `rollForward: Major`; runs on our .NET 10.0.12. BuildHost for MSBuild is `net8.0` rollForward Major. |
| Size | 276 MB installed (our whole .deb is 37 MB). |
| Install | `dotnet tool install roslyn-language-server --version <pinned> --tool-path <app data dir>`; no global install, no VS Code. |
| Transport | `--stdio` (or `--pipe`); logs via `window/logMessage` and `--extensionLogDirectory`. |
| Telemetry | Default **all**; we must pass `--telemetryLevel off`. |
| Projects | `--autoLoadProjects` off by default. Explicit open: notification `solution/open` `{ solution: uri }`; the server answers with `workspace/projectInitializationComplete`. |
| Daemon | `--daemon-mode`: one shared process per user and version, outlives the editor (keep-alive). Not used. |
| GC | `System.GC.Server: true` in the server's runtimeconfig. |
| Encoding | Server does not announce `positionEncoding` → UTF-16 (LSP default). Sync kind 2 (incremental). |
| Triggers | Completion: `" ( : [ \ { # . > space ~ <`; signature help: `( , [ < {`, retrigger `) ] > }`. |
| Side effect | A `.cs` file outside the opened solution is treated as a file-based program: the server writes a `Canonical.csproj` to `/tmp/roslyn-canonical-misc/` and runs **`dotnet restore`** on it. |

## 3. Proof of concept (tools/lsp-poc)

`poc.py` and `measure.py` drive the real server over stdio: initialize → initialized → `solution/open` → wait for
`workspace/projectInitializationComplete` → didOpen → completion / resolve / signature help / hover / pull
diagnostics → shutdown / exit.

Results:

- Sample solution (3 projects, net11.0, CPM):
  - load 1.1–1.5 s;
  - `calc.` (a type from another project) → `Divide, Sum, Twice, …` in 5–7 ms;
  - `completionItem/resolve` → `int Sample.Core.Calculator.Sum(int a, int b)`;
  - `Sum(1, ` → active parameter 1;
  - clean exit (code 0).
- a private backend solution (`.slnx`, 95 projects):
  - `projectInitializationComplete` after 5–5.7 s;
  - completion 39 items (first request 250 ms, then 1–10 ms);
  - hover and pull diagnostics work.
- Notes for the client:
  - `itemDefaults` (editRange, data, commitCharacters) must be merged into an item before `completionItem/resolve`.
    Without it the server throws in `CompletionResolveHandler`.
  - A `.`-triggered request whose position has an identifier after it returns `null`. Invoked completion
    (triggerKind 1) at the same place returns the list. The editor only sends `.` triggers at the end of a word.
  - The server answers 77 + 6 `workspace/configuration` items (C#, VB, Razor options); `null` values are accepted.

### Memory (RSS of the server process and its children, reference solution)

| | Server GC (default) | Workstation GC |
|---|---|---|
| After project load | 907 MB | 885 MB |
| Peak | 1313 MB | 1307 MB |
| Steady, 15 s after the first requests | 1355 MB | 1278 MB |

For comparison, today's single process (GUI + in-process workspace) peaked at 1.37 GB on the same repo, including
Find References. With the server, the GUI process would drop its workspace (estimated 300–400 MB GUI + SQLite).
The total is about the same as today, not lower. The point of the migration is the feature set and the
maintenance, not memory.

## 4. Proposed architecture

- New folder `LightweightIDE.Language/Lsp` (no new project):
  - `JsonRpcConnection`: Content-Length framing, request correlation, server requests, `$/cancelRequest`.
  - `LanguageServerProcess`: start / health / restart with backoff.
  - `LspClient`: lifecycle and capabilities.
  - `DocumentSync`: versions, incremental didChange, `PositionConverter` (UTF-16).
- Feature services keep their current interfaces (`CompletionProvider`, `NavigationService`, …) with an LSP
  implementation. The GTK side changes little.
- The SQLite index stays as it is. `MSBuildWorkspace` is removed from the GUI process when the LSP path is complete,
  so there is no second workspace.
- Server shipped next to the app: pinned version in `packaging/roslyn-language-server.version`. `install.sh` and the
  .deb install it with `--tool-path` (as with netcoredbg). Started with
  `--stdio --telemetryLevel off --autoLoadProjects 0` plus `solution/open` for the solution the user picked.

## 5. Dependencies

- Runtime: `roslyn-language-server` 5.13.0-1.26514.10 (pinned, prerelease), .NET 10 runtime (already required).
- No new NuGet packages: System.Text.Json and Process are enough.
- The Roslyn NuGet packages (Microsoft.CodeAnalysis.*) leave the GUI after the switch. That makes the GUI build
  smaller.

## 6. Risks

1. **Smart completion is not an LSP feature.**
   - Today's ★ suggestions use the semantic model directly: implementations for `AddScoped<IShop, |>`, and `new` for
     interfaces.
   - Over LSP the client only has completion items. Keeping the ★ suggestions needs one of:
     - a server extension (`--extension`, MEF in the server's process; API not public, may break between versions);
     - a second, small semantic source. The spec forbids that.
   - Same for the `CodeModel` index (implementations), which LSP replaces only partly with
     `textDocument/implementation`.
2. Prerelease only: options and behaviour "subject to change"; we pin and test before each bump.
3. Silent side effects: file-based program restore for loose `.cs` files (needs a setting to turn it off, or open
   only files that belong to the solution); MSBuild evaluates project files (trusted folders only, as today).
4. Size: +276 MB installed (the .deb grows from 37 MB to about 120 MB compressed), or a first-run download.
5. Custom Roslyn notifications (`solution/open`, `workspace/projectInitializationComplete`,
   `window/_roslyn_showToast`) are not part of the LSP spec; they follow the VS Code extension.

## 7. Plan (per the brief's milestones)

- M2:
  - JSON-RPC transport + process manager + lifecycle;
  - tests for framing, partial reads, concurrency, cancellation, crash.
- M3:
  - document sync + `PositionConverter`;
  - tests for Unicode, emoji, CRLF.
- M4: completion (TextEdit, additionalTextEdits, resolve, stale-response guard), signature help, hover, pull or
  push diagnostics.
- M5: definition / typeDefinition / implementation / references / workspace symbols on the LSP.
- M6: code actions + resolve, rename, formatting, semantic tokens.
- M7: measurements on the reference solution (GUI + server RSS, latency) against today's numbers.
- M8: crash recovery, settings, packaging, docs; remove `MSBuildWorkspace` from the GUI.

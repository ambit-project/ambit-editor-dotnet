# In-process Roslyn vs roslyn-language-server (LSP): comparison

**Decision (owner, 2026-11-20): stay on the in-process Roslyn engine.** The LSP engine, its tests and
`tools/EngineCompare` stay on branch `feat/lsp`. Fixes that did not depend on LSP were moved to `main`: System.Object
members ranked below the type's own members, and completion re-opening after `,` / space / `<` in generic arguments.

Date: 2026-11-20. Branch `feat/lsp`. Raw results: `docs/plans/compare/large-solution-*.json`.
How to repeat:
`dotnet run --project tools/EngineCompare -c Release -- <folder> Roslyn|Lsp <out.json>`, one engine per process.

## Setup

- **Solution:** a private backend solution (`.slnx`), 95 projects.
- **Machine:** laptop, 16 threads, 32 GB, .NET 10.0.12, SDK 11.0.100.
- **Engines:**
  - "Roslyn" — `MSBuildWorkspace` in the IDE process plus the `CodeModel` background analysis, as on `main`.
  - "Lsp" — `roslyn-language-server` 5.13.0-1.26514.10 as a child process with Workstation GC. The IDE process
    keeps no workspace.
- **Same code path:** both engines go through the services the editor calls (`CompletionProvider`,
  `SignatureHelpProvider`, `NavigationService`, `DiagnosticsService`) and the same ranking (`CompletionRanker`).
- **Scenarios:** picked by the tool from the solution's own code. 21 runs each (references: 5). Every run is a new
  document version, so no cached answer is reused.
  - member completion: `repository.` in a command handler, expected `GetAsync`;
  - completion with 3 letters typed;
  - a type that is not imported: `new StringBuil` without `using System.Text`;
  - ★ DI registration: `AddSingleton<IMailSettingsStore, |`;
  - signature help;
  - hover;
  - diagnostics of the file;
  - definition, references and implementations.

## Latency (ms; first request / p50 / p95 of the following ones)

| Scenario | Roslyn in process | LSP |
|---|---|---|
| Solution load (until features work) | 9 736 (+ 22 800 background analysis) | **5 347** |
| Member completion after `.` | 301 / 22 / 45 | 710 / **8** / **18** |
| Completion, 3 letters typed | 19 / 18 / 31 | **8 / 8 / 19** |
| Unimported type (`StringBuil`) | 166 / 35 / 78 | **49 / 38 / 57** |
| ★ `AddSingleton<IMailSettingsStore, \|` | **36 / 11 / 20** | 2 920 / 16 / 26 (cached after the first) |
| Signature help | **22 / 15 / 30** | 75 / 34 / 61 |
| Hover | 57 / **20 / 26** | **33** / 27 / 42 |
| Diagnostics of the file | **61 / 15 / 20** (compiler only) | 125 / 90 / 101 (compiler + analyzers) |
| Go to definition | 25 / 14 / 21 | **18 / 3 / 12** |
| Find references (7 results) | **2 417 / 1 357 / 1 398** | 7 009 / 3 380 / 3 432 |
| Implementations (2 results) | **1 155 / 950 / 1 115** | 1 829 / 1 126 / 1 473 |

All interactive operations stay under 100 ms p95 on both engines. The exceptions are references and implementations,
which search the whole solution: LSP is 2.5× slower there. The first ★ list over LSP takes about 3 s. It needs
`textDocument/implementation`, which the in-process engine answers from its index. Later keystrokes use a
one-minute cache.

## Quality (same ranking code)

| Scenario | Roslyn in process | LSP |
|---|---|---|
| Member completion | `GetAsync` 1st (48 items, incl. extension methods) | `GetAsync` 1st (5 items) |
| 3 letters typed | 1st | 1st |
| Unimported type | `StringBuilder` 1st (13 446 items) | `StringBuilder` 1st (list cut to 1 000 by the server) |
| ★ DI registration | `MailSettingsStore` 1st ★ | `MailSettingsStore` 1st ★ |
| Signature help | 2 overloads, active parameter | 2 overloads, active parameter (also generic `<…>`) |
| Diagnostics | compiler | compiler + IDE / CA analyzers (e.g. IDE0059, CA1822) |
| Definition / references / implementations | 1 / 7 / 2 | 1 / 7 / 2 |

## Memory (RSS; LSP = IDE process + server process tree)

| | Roslyn in process | LSP |
|---|---|---|
| After load | 803 MB | **549 MB** (IDE 119 + server) |
| Steady after all scenarios | **1 314 MB** | 1 515 MB (IDE 119 + server 1 396) |
| Peak | **1 454 MB** | 2 305 MB |
| Server GC (server default) instead of Workstation | — | steady 1 998 MB, peak 2 375 MB |

- **More memory with LSP.**
  - The steady state after the same work is 15% higher, and the peak is 59% higher (during Find References).
  - The in-process number already includes the full background compilation of all 95 projects.
- **Lighter at startup.** The IDE process itself stays at about 120 MB, and the solution is usable 4.4 s sooner.
- **Server GC is not worth it.** The server's default Server GC costs another 450 MB for the same latencies. That is
  why the client starts the server with Workstation GC (`Language:LanguageServerServerGc` turns it back on).

## What the migration needed (found while building it)

1. **Completion does not warm up the semantic model by itself.** Roslyn answers completion on "frozen partial"
   semantics.
   - Members of a type from another project stay unknown, and the result is `null`, until a full-semantics request
     has touched that type. Pulling diagnostics of the document is not enough.
   - The empty answer is cached per document version.
   - The client therefore warms up with a hover on the receiver, re-sends the same text as a new version, and asks
     again.
2. **Unimported types depend on configuration.** They only come when the client answers `workspace/configuration`
   with `dotnet_show_completion_items_from_unimported_namespaces = true`; `null` means off.
3. **Parameter labels are bare names.** Signature help sends `"a"`, not offsets, even with `labelOffsetSupport`.
   The client splits the parameter list itself.
4. **Implementation locations have no length.** `textDocument/implementation` returns zero-length ranges, so the
   client reads the name from the source.
5. **Empty prefix gives a cut list.** The server returns at most 1 000 items (`isIncomplete`). ★ types missing from
   it are added by the client.
6. **Invalid command-line value.** `--autoLoadProjects 0` makes the server exit. Leave the flag out and send
   `initializationOptions.autoLoadProjects = 0`.
7. **Two transport bugs found by the tests.**
   - Reflection-based JSON serialization has to be enabled explicitly.
   - `Channel.Reader.Count` throws on a single-reader channel and silently stopped the writer.

## What LSP does not cover yet on this branch

- **Features still on the in-process workspace.** Rename, code actions (Alt+Enter), formatting via Roslyn,
  "Analyze project" and the `CodeModel` index load that workspace on first use. Using any of them doubles memory
  (both workspaces).
- **Not implemented yet:** semantic tokens and code actions / rename over LSP (milestone 6 of the plan).

## Assessment

- **Where LSP wins:**
  - faster start;
  - an IDE process that stays small;
  - analyzer diagnostics for free;
  - generic signature help;
  - typed completion and go to definition as fast or faster;
  - updates by bumping a pinned server version instead of tracking Roslyn APIs ourselves.
- **Where LSP loses:**
  - more total memory (+15% steady, +59% peak);
  - solution-wide searches 2.5× slower;
  - the first ★ list costs about 3 s;
  - the client needs the workarounds above, on a prerelease-only server whose behaviour may change.
- **The project's aim was "lighter than Rider".** Measured on the reference solution, the in-process engine is lighter. LSP is
  not a memory win. It is a maintenance and feature trade.

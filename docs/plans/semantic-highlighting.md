# Semantic highlighting and themes: research

Date: 2026-11-20. Status: done (steps 1–5).

## Problem

Colors come only from GtkSourceView's C# grammar (lexical): keywords, strings, comments, numbers, built-in types.
Every identifier has the same color, so these all look alike: class, interface, method, property, field, local,
parameter, namespace, enum member, static member. Rider and VS color them by meaning.

## What Roslyn gives (measured on the reference solution)

`Classifier.GetClassifiedSpansAsync(document, span)` is public API (Microsoft.CodeAnalysis.Workspaces). It is the
same classifier Visual Studio uses.

Classifications in the 3 largest files (counts):

| Group | Classifications |
|---|---|
| Types | class name, record class name, struct name, interface name, enum name, delegate name, type parameter name |
| Members | method name, extension method name, property name, field name, constant name, event name, enum member name |
| Variables | local name, parameter name |
| Other | namespace name, static symbol (additive), keyword - control (if / return / await …), string - escape character, operator - overloaded, label, xml doc comment parts, regex / JSON in strings |

Cost:

| File | Lines | Spans | Whole file (warm) | 80-line window |
|---|---|---|---|---|
| ApiEndpoints.cs | 1 512 | 14 232 | 60 ms | 3 ms |
| BrandsSeeder.cs | 1 959 | 10 102 | 372 ms | 17 ms |
| PublicDemoSeeder.cs | 1 796 | 9 414 | 422 ms | 8 ms |

The first request on a cold project takes 1–10 s while the project compiles.

Therefore:
- classify **only the visible lines** (plus a margin);
- after typing, debounce 150 ms and classify again;
- on scroll, classify the newly visible lines;
- before the project has compiled, show only the lexical colors.

## How it fits GtkSourceView

- **Tags on top of the lexical colors.** Semantic colors are `GtkTextTag`s over the GtkSourceView highlighting,
  the same way the debugger and diagnostics marks work today.
  - The tags are created after the source tags and get a higher priority, so they win where both color a word.
  - Only the visible range is cleared and re-tagged.
- **Colors come from the style scheme, with a fallback.**
  - Style id, e.g. `lw:class`, `lw:interface`, `lw:method`, `lw:local`, `lw:parameter`, `lw:field`, `lw:property`.
    GtkSourceView schemes accept extra style ids.
  - When a scheme does not define them (Adwaita, Kate, Solarized…), each has a fallback: `def:type`,
    `def:function`, `def:identifier`, or a built-in palette for light / dark.
- **Own themes.** The IDE ships two schemes, "Lightweight Dark" and "Lightweight Light", with semantic colors close
  to Rider / VS Code Dark+ (types teal, interfaces lighter teal, methods yellow, locals and parameters light blue,
  fields and properties softer, statics italic, control keywords purple). Installed in the app's
  `styles/` folder and added to `StyleSchemeManager`.
- **Theme choice.** View → Color Theme: the two own schemes plus the installed ones (Adwaita, classic, cobalt,
  kate, oblivion, solarized, tango), dark / light following the system or fixed. The choice is saved in the IDE
  state.

## Plan

1. **SemanticHighlighter** (Language):
   - API `GetSpansAsync(path, text, start, end)`, returning (start, end, kind) with UTF-16 offsets;
   - kinds mapped from Roslyn classifications to about 15 IDE kinds;
   - static symbol as a modifier.
2. **EditorTab:**
   - visible range from `GetVisibleRect` + `GetLineAtY`;
   - debounce 150 ms after a change and 50 ms after a scroll;
   - tags per kind with priority above source tags;
   - re-tag only the range that was classified;
   - stale results (old document version) are dropped.
3. **Schemes:**
   - `packaging/styles/lightweight-dark.xml` and `lightweight-light.xml`, installed with the app;
   - fallback palette for other schemes;
   - View → Color Theme menu with the choice saved.
4. **Tests:**
   - the mapping (class / interface / local / parameter / static) on code samples;
   - the visible-range logic;
   - Xvfb screenshots light / dark on a real file from the reference solution;
   - UI watchdog: no stalls above 50 ms while scrolling a 2 000-line file.
5. **Usages and unused code (done):**
   - **Occurrences of the symbol under the caret** (`SymbolUsages`, option `Editor:HighlightUsages`).
     - Highlighted 250 ms after the caret stops; the declaration gets its own color (`lw:usage-definition`).
     - Only the file's semantic model is used: identifiers with the same name are resolved and compared.
       `SymbolFinder.FindReferences` limited to the document still cascades to implementations across the
       solution and took up to 4 s on the reference solution; the model comparison takes 0–90 ms (1.4 s once, for a name used in
       29 large lambdas, off the UI thread).
     - A constructor counts as its type; nothing is shown for a single occurrence or on `var`.
   - **Faded unused code** (`UnnecessaryCode`, option `Editor:FadeUnusedCode`).
     - Covers IDE0005 (usings), IDE0051 / IDE0052 (private members), IDE0059 (value never read) and IDE0060
       (parameter). The analyzers are the Visual Studio ones from Microsoft.CodeAnalysis(.CSharp).Features.
     - The faded range comes from the diagnostic's "Unnecessary" locations, e.g. a whole unused method. The tooltip
       shows the rule and message.
     - The analysis runs after the file's diagnostics, only for files of a loaded project, and is cancelled by the
       next edit. Cost on the reference solution: 4–200 ms per file, 2 s for ApiEndpoints.cs (1 500 lines of minimal API lambdas).

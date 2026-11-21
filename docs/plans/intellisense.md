# IntelliSense — plan

Goal: writing C# in a large solution (reference: a private backend solution with 95 projects) is as fluent as in Rider for daily work.
Without it the IDE is a code viewer. Engine: Roslyn (the C# compiler and the same completion / analysis services
Visual Studio uses); the IDE adds its own ranking, context ("smart") logic, signature help and presentation on top.

## Why the first version felt weak (found 2026-11-20)

- Roslyn returned ~3700 items with priorities, expected-type matches and unimported types; the IDE dropped that data
  and sorted alphabetically, so `AbandonedMutexException` was first.
- Completion opened only after `.` and at the start of a word; no signature help, no item description.
- Nothing used the context of the file: implementations of interfaces, members of services, generic constraints.

## Targets (measured on the reference solution, warm workspace)

| What | Target |
|---|---|
| Completion list after a keystroke | p95 < 150 ms |
| Signature help after `(` / `,` | p95 < 80 ms |
| Inheritance markers for an open file | < 1 s, in the background |
| UI thread stalls | none > 250 ms (watchdog in the log) |
| Right item in the first 3 rows | for the scenarios below |

## Scenarios that must work (each one a test or a scripted Xvfb check)

1. `services.AddScoped<IShopService, |` → classes implementing `IShopService` first (★, "implements IShopService").
2. `IShopService shop = new |` → implementing classes first.
3. `_shopService.|` → the service's methods and properties first, picked-before members above the rest.
4. `var sb = new StringB|` → `StringBuilder` even without `using System.Text`; committing adds the using.
5. `Color c = |` → `Color.Red`, … first (expected type).
6. Locals and parameters before types; keywords last unless typed.
7. `Foo(|` → parameters of all overloads, the active one bold, documentation; `,` moves to the next parameter.
8. Hover on a type / variable → its documentation, usable members and (for an interface) its implementations.
9. Gutter marks on an interface / abstract / virtual member (↓ implementations) and on implementing / overriding code
   (↑ base); hover lists them, Ctrl+click navigates.
10. Picking an item: the selected item is the most likely one (preselect / expected type / recently used).

## Phases

### Phase 1 — completion and signature help (this round)
- Own ranking (`CompletionRanker`): match buckets (exact, prefix, CamelCase humps `SB`→`StringBuilder`, word part,
  substring, fuzzy), then relevance: smart / expected type > Roslyn preselect > locals > members > types in scope >
  keywords > unimported types; picks are remembered.
- Smart types (`SmartTypes`): generic constraints of any method / type at the caret, `new` with an abstract
  expected type.
- Item description under the list (Roslyn `GetDescriptionAsync`).
- Triggers: every character Roslyn would trigger on (`.`, space after `new`, `<`, letters), filter while typing.
- Signature help (own, on the semantic model): overloads, active parameter, named arguments, docs.
- Tests for each scenario above that does not need the UI; Xvfb check on the reference solution.

### Phase 2 — understanding the open file
- Inheritance gutter marks (↓ / ↑) with hover list and Ctrl+click.
- Hover: members one can use and implementations.
- Semantic highlighting (Roslyn classifier): types, interfaces, methods, locals, parameters, fields, statics.

### Phase 3 — analysis while typing
- Roslyn IDE analyzers and the project's analyzers in live diagnostics (unused usings faded, IDE00xx hints).
- Nullable warnings surfaced like in the build.

### Phase 4 — speed of writing
- Postfix templates (`.var`, `.if`, `.foreach`, `.return`), live templates (`prop`, `ctor`, `for`), method
  completion inserting `()` with the caret inside, override / partial completion check.

## Not planned
- A completely new C# engine: Roslyn is the compiler itself; a hand-written one would always lag the language.
- AI completion.

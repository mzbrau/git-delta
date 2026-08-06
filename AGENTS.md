### Overview & Scope

Cross-platform desktop Git client focused on code review. Stack: .NET 10, Avalonia 12, CliWrap (`git` CLI), CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, NUnit. Solution: `src/GitDelta.slnx`.

This file applies to the whole repo: `src/`, `tests/`, root props, `.github/`. No nested `AGENTS.md`. Closest `AGENTS.md` wins if one is added later.

Phases 1–3 are in-tree (local Git, GitHub PR review, AI). Design intent remains in `README.md`, `Plan-Phase1.md`, `Plan-Phase2.md`, `Plan-Phase3.md` — do not treat plans as a checklist of unfinished work.

Goals: high unit + integration coverage; maintainable SRP-sized collaborators; UI never does Git/IO/CPU-heavy work on the Avalonia sync context.

### Agent Role

Experienced .NET / Avalonia desktop engineer for this codebase.

Allowed: read/edit product and test code under `src/` and `tests/`; run restore/build/scoped tests; add packages only via central versioning when required for the task.

Not allowed: invent scripts/tools not in the repo; add LibGit2Sharp or ReactiveUI; put Avalonia refs in Core/Git/Diff; commit/push/tag unless asked; rewrite plan docs or `reference/` unless asked.

### Build, Test & Validation Commands

Requires .NET 10 SDK and `git` on PATH (see `README.md`).

```bash
dotnet restore src/GitDelta.slnx
```

```bash
dotnet build src/GitDelta.slnx -c Debug
```

```bash
dotnet build src/GitDelta.slnx -c Release
```

No dedicated lint/format pipeline (no `.editorconfig`, `dotnet format` not used in CI). Treat Release build warnings-as-errors (`Directory.Build.props`) as the check.

```bash
dotnet test src/GitDelta.slnx -c Debug
```

Prefer scoped / filtered tests while iterating:

```bash
dotnet test tests/GitDelta.Core.Tests/GitDelta.Core.Tests.csproj -c Debug --no-restore
```

```bash
dotnet test tests/GitDelta.Git.Tests/GitDelta.Git.Tests.csproj -c Debug --no-restore
```

```bash
dotnet test tests/GitDelta.Diff.Tests/GitDelta.Diff.Tests.csproj -c Debug --no-restore
```

```bash
dotnet test tests/GitDelta.App.Tests/GitDelta.App.Tests.csproj -c Debug --no-restore
```

```bash
dotnet test tests/GitDelta.IntegrationTests/GitDelta.IntegrationTests.csproj -c Debug --no-restore
```

```bash
dotnet test tests/GitDelta.Core.Tests/GitDelta.Core.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~Architecture"
```

```bash
dotnet run --project src/GitDelta.App
```

Publish (CI; slow) (unverified):

```bash
dotnet publish src/GitDelta.App/GitDelta.App.csproj -c Release -r win-x64 --self-contained true -o ./artifacts/win-x64
```

```bash
dotnet publish src/GitDelta.App/GitDelta.App.csproj -c Release -r win-arm64 --self-contained true -o ./artifacts/win-arm64
```

Benchmarks (slow; not in CI) (unverified):

```bash
dotnet run --project tests/GitDelta.Benchmarks -c Release
```

### Conventions & Patterns

- Projects: `GitDelta.Core` (domain, settings, abstractions), `GitDelta.Git` (CliWrap + porcelain), `GitDelta.Diff` (patch/FileDiff/row projection/syntax), `GitDelta.GitHub` (GraphQL), `GitDelta.Persistence` (tokens + SQLite), `GitDelta.Review` (PR sessions / outbox / `IReviewTree`), `GitDelta.AI` (Copilot agent + review coordination), `GitDelta.App` (Avalonia UI + DI root).
- Tests mirror projects under `tests/`; shared fixtures in `tests/GitDelta.TestSupport` (`RepositoryBuilder`). `TestSupport` is not a test project.
- Namespaces match assembly (`GitDelta.Core`, `GitDelta.Git`, …). Prefer file-scoped namespaces.
- Git interfaces live in `src/GitDelta.Core/Abstractions/`; implementations in `Git` / `Diff`. Register via `AddGitDeltaGit()` / `AddGitDeltaDiff()` and `ServiceConfiguration.Build()`.
- MVVM: `CommunityToolkit.Mvvm` (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`). Views under `Views/`, viewmodels under `ViewModels/`, custom controls under `Controls/`. Large ViewModels use nested collaborators (e.g. `WorkingCopyDiffPresenter`) while the façade keeps AXAML bindings.
- Shared helpers: `DiffPresentation` for options/intra-line/projection; `AiReviewSessionViewModel` for shared AI chrome formatting (PR + pending changes).
- Package versions only in `Directory.Packages.props` (`ManagePackageVersionsCentrally`). Project files reference packages without `Version=`.
- Target `net10.0`; nullable enabled; implicit usings on.
- All Git goes through CliWrap / `IGitProcessRunner` — no shell string concat, pass `CancellationToken`, prefer porcelain/NUL machine output for parsers. Outside `GitDelta.Git`, stream large stdout via `GitProcessOptions.StdoutFilePath` (never `using CliWrap`).
- Enforce via architecture tests: Core/Git/Diff/GitHub/Review/Persistence/AI must not reference Avalonia; CliWrap only from `GitDelta.Git`; Copilot SDK only from AI; App must not take `IGitProcessRunner`.
- Search/edit excludes: `**/bin/`, `**/obj/`, `artifacts/`, `TestResults/`, `.git/`.
- Binary assets (`*.png`, `*.ico`, `*.icns`, …) are Git LFS (`.gitattributes`).

### Soft size / SRP

- Prefer collaborators over growing ViewModels past ~800–1000 lines of logic. Existing large façades: extract on touch when adding substantial behavior; do not drive-by rewrite entire files outside the task.
- New AI UX belongs in shared helpers/presenters, not a second copy of Review methods on PendingChanges.
- Diff presentation toggles (view mode, show full file, collapse) must reproject in memory — never invalidate warm caches or re-run git unless content identity changed.

### Async / race rules

- Any async load that mutates ViewModel/UI state must use CTS ownership (`ReferenceEquals` on the generation CTS and selection identity) before writing; superseded `finally` blocks must not clear loading flags for newer work.
- Prefer cancel-and-replace for refresh/inbox over “if busy, return”.
- Pass `CancellationToken` through Git/network/AI; no fire-and-forget without documenting ownership and cancellation.

### UI thread / performance

- Keep UI off the Git sync context; Git ops async + cancellable. `GitProcessRunner` registers with `assertNoUiSyncContext: true`.
- All patch-producing git calls must set `MaxStdoutBytes` (or equivalent); untracked file reads must honor the same byte budget (`MaxDiffPatchBytes`).
- Diff row projection, intra-line enrich, and syntax tokenization run off the UI thread (`Task.Run` or dedicated services); marshal a single collection `Reset` back.
- Do not buffer unbounded Git stdout into strings when streaming/`MaxStdoutBytes` / `StdoutFilePath` exist.
- Avoid quadratic list rebuilds on the UI thread; use set lookups for overlays.

### Testing expectations

- New Git porcelain/mutating API → `RepositoryBuilder` integration test in the matching test project.
- New ViewModel async load path → ownership/cancellation test where races are possible.
- Shared behavior extracted from WC/Review → test the collaborator/helper once; thin façade tests only for wiring.
- Before finishing a change: Release build (warnings-as-errors) + architecture tests + affected project tests.

### Dos and Don’ts

- Do put new Git capability behind Core interfaces; keep App ignorant of CliWrap details.
- Do use `RepositoryBuilder` for temp repos in tests; pin identity/EOL as existing helpers do.
- Do run architecture + affected project tests before finishing a change.
- When adding features to WC/Review/PendingChanges/AI coordinator, extract or extend a focused collaborator rather than appending hundreds of lines to the façade.
- Do not duplicate AI side-panel / chat / annotation flows between PR and pending changes.
- Don’t add LibGit2Sharp, ReactiveUI, or Generic Host.
- Don’t add Avalonia usings/packages to Core/Git/Diff/GitHub/Review/Persistence/AI.
- Don’t edit `bin/`, `obj/`, or generated Verify/coverage outputs.
- Don’t expand GitHub/AI scope unless the task asks; when touching those areas, follow shared-presenter and test rules above.
- Don’t drive-by refactor large files (e.g. entire `WorkingCopyViewModel`) outside the task.

### Safety & Guardrails

Off-limits without explicit ask: force-push, rewriting `main` history, secrets/tokens, notarisation/signing/installer work, large unrelated refactors, editing `reference/` mockups or `resources/` binaries unless required.

Safe to automate: restore, Debug/Release build, unit/integration tests, focused code edits, adding NUnit tests.

Command constraints:
- Prefer scoped `dotnet test …/<Project>.csproj` over full solution while iterating.
- Avoid full `dotnet publish` and BenchmarkDotNet locally unless needed.
- Do not run interactive/global package installs beyond `dotnet restore` for this solution.

Never edit: `**/bin/**`, `**/obj/**`, vendored/build outputs, `.git/` internals.

### Git & PR Rules

- Default branch: `main`. Work on feature branches; open PRs into `main`.
- Commit messages: short imperative subject (match history, e.g. `Add history view`). No required Conventional Commits prefix.
- PRs: CI must pass (`.github/workflows/ci.yml`: restore, Release build, tests, Windows publish). No PR template — include summary + test plan.
- CI uses `fetch-depth: 0` for MinVer; do not shallow-clone in release/version workflows.
- Do not commit unless the user asks.

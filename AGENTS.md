### Overview & Scope

Cross-platform desktop Git client focused on code review. Stack: .NET 10, Avalonia 12, CliWrap (`git` CLI), CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, NUnit. Solution: `src/CodeReviewr.slnx`.

This file applies to the whole repo: `src/`, `tests/`, root props, `.github/`. No nested `AGENTS.md`. Closest `AGENTS.md` wins if one is added later.

Design intent: `README.md`, `Plan-Phase1.md`, `Plan-Phase2.md`. Do not treat plans as implemented features.

### Agent Role

Experienced .NET / Avalonia desktop engineer for this codebase.

Allowed: read/edit product and test code under `src/` and `tests/`; run restore/build/scoped tests; add packages only via central versioning when required for the task.

Not allowed: invent scripts/tools not in the repo; add LibGit2Sharp or ReactiveUI; put Avalonia refs in Core/Git/Diff; commit/push/tag unless asked; rewrite plan docs or `reference/` unless asked.

### Build, Test & Validation Commands

Requires .NET 10 SDK and `git` on PATH (see `README.md`).

```bash
dotnet restore src/CodeReviewr.slnx
```

```bash
dotnet build src/CodeReviewr.slnx -c Debug
```

```bash
dotnet build src/CodeReviewr.slnx -c Release
```

No dedicated lint/format pipeline (no `.editorconfig`, `dotnet format` not used in CI). Treat Release build warnings-as-errors (`Directory.Build.props`) as the check.

```bash
dotnet test src/CodeReviewr.slnx -c Debug
```

Prefer scoped / filtered tests while iterating:

```bash
dotnet test tests/CodeReviewr.Core.Tests/CodeReviewr.Core.Tests.csproj -c Debug --no-restore
```

```bash
dotnet test tests/CodeReviewr.Git.Tests/CodeReviewr.Git.Tests.csproj -c Debug --no-restore
```

```bash
dotnet test tests/CodeReviewr.Diff.Tests/CodeReviewr.Diff.Tests.csproj -c Debug --no-restore
```

```bash
dotnet test tests/CodeReviewr.App.Tests/CodeReviewr.App.Tests.csproj -c Debug --no-restore
```

```bash
dotnet test tests/CodeReviewr.IntegrationTests/CodeReviewr.IntegrationTests.csproj -c Debug --no-restore
```

```bash
dotnet test tests/CodeReviewr.Core.Tests/CodeReviewr.Core.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~Architecture"
```

```bash
dotnet run --project src/CodeReviewr.App
```

Publish (CI; slow) (unverified):

```bash
dotnet publish src/CodeReviewr.App/CodeReviewr.App.csproj -c Release -r win-x64 --self-contained true -o ./artifacts/win-x64
```

```bash
dotnet publish src/CodeReviewr.App/CodeReviewr.App.csproj -c Release -r win-arm64 --self-contained true -o ./artifacts/win-arm64
```

Benchmarks (slow; not in CI) (unverified):

```bash
dotnet run --project tests/CodeReviewr.Benchmarks -c Release
```

### Conventions & Patterns

- Projects: `CodeReviewr.Core` (domain, settings, abstractions), `CodeReviewr.Git` (CliWrap + porcelain), `CodeReviewr.Diff` (patch/FileDiff/row projection/syntax), `CodeReviewr.App` (Avalonia UI + DI root).
- Tests mirror projects under `tests/`; shared fixtures in `tests/CodeReviewr.TestSupport` (`RepositoryBuilder`). `TestSupport` is not a test project.
- Namespaces match assembly (`CodeReviewr.Core`, `CodeReviewr.Git`, …). Prefer file-scoped namespaces.
- Git interfaces live in `src/CodeReviewr.Core/Abstractions/`; implementations in `Git` / `Diff`. Register via `AddCodeReviewrGit()` / `AddCodeReviewrDiff()` and `ServiceConfiguration.Build()`.
- MVVM: `CommunityToolkit.Mvvm` (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`). Views under `Views/`, viewmodels under `ViewModels/`, custom controls under `Controls/`.
- Package versions only in `Directory.Packages.props` (`ManagePackageVersionsCentrally`). Project files reference packages without `Version=`.
- Target `net10.0`; nullable enabled; implicit usings on.
- All Git goes through CliWrap / `IGitProcessRunner` — no shell string concat, pass `CancellationToken`, prefer porcelain/NUL machine output for parsers.
- Enforce: Core/Git/Diff must not reference Avalonia (architecture test).
- Search/edit excludes: `**/bin/`, `**/obj/`, `artifacts/`, `TestResults/`, `.git/`.
- Binary assets (`*.png`, `*.ico`, `*.icns`, …) are Git LFS (`.gitattributes`).

### Dos and Don’ts

- Do keep UI off the Git sync context; Git ops async + cancellable.
- Do put new Git capability behind Core interfaces; keep App ignorant of CliWrap details.
- Do use `RepositoryBuilder` for temp repos in tests; pin identity/EOL as existing helpers do.
- Do run architecture + affected project tests before finishing a change.
- Don’t add LibGit2Sharp, ReactiveUI, or Generic Host.
- Don’t buffer huge Git stdout into strings when streaming APIs exist (`GitProcessOptions`, long-lived processes).
- Don’t add Avalonia usings/packages to Core/Git/Diff.
- Don’t edit `bin/`, `obj/`, or generated Verify/coverage outputs.
- Don’t implement Phase 2/3 (GitHub PRs, AI) unless the task explicitly asks.
- Don’t drive-by refactor large files (e.g. `WorkingCopyViewModel`) outside the task.

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

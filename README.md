# GIT DELTA

A fast, cross-platform Git client focused on code review.

Built with .NET 10 and Avalonia 12.

## Documentation

User documentation (Docusaurus) lives under [`docs/`](docs/). The site project is [`docs/docusaurus/`](docs/docusaurus/).

Published docs: [https://mzbrau.github.io/git-delta/](https://mzbrau.github.io/git-delta/)

```bash
cd docs/docusaurus
npm install
npm start
```

## Requirements

- **Git 2.30 or later, installed and available on the PATH.** GIT DELTA does not bundle Git; it drives the `git` command line directly so that credential helpers, hooks, `.gitattributes` filters (including Git LFS), and `core.fsmonitor` all behave exactly as they do in your terminal. If Git cannot be found, GIT DELTA will tell you at startup rather than failing part-way through an operation. The Git executable path can be overridden in settings for non-standard installations.
- Windows or macOS. Builds are self-contained, so no .NET runtime install is needed for published artifacts. Development requires the .NET 10 SDK.

### Installing on Windows

Download the latest Windows setup executable from [GitHub Releases](https://github.com/mzbrau/git-delta/releases) and run it. Installers are packaged with Velopack and target `win-x64`. Running a newer Setup.exe upgrades an existing install.

To cut a release, push a SemVer tag on `main` with the `v` prefix (for example `v0.1.0`). MinVer supplies the version; the Release workflow builds, packages, and uploads the installer.

### Installing Git

| Platform | Command |
| --- | --- |
| Windows | `winget install --id Git.Git`, or download from [git-scm.com](https://git-scm.com/download/win) |
| macOS | `brew install git`, or install the Xcode Command Line Tools |

Verify with:

```bash
git --version
```

## Development

```bash
dotnet restore src/GitDelta.slnx
dotnet build src/GitDelta.slnx
dotnet test src/GitDelta.slnx
dotnet run --project src/GitDelta.App
```

### Observability (Aspire)

For local traces and metrics (diff load/present/project, paint duration), run the Aspire AppHost — it starts the desktop app and the Aspire dashboard:

```bash
dotnet run --project src/GitDelta.AppHost
```

OpenTelemetry export is enabled only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (Aspire injects this). Normal `dotnet run --project src/GitDelta.App` does not require Aspire.

### Solution layout

| Project | Role |
| --- | --- |
| `GitDelta.Core` | Domain model, settings, Git/diff abstractions, AI contracts |
| `GitDelta.Git` | CliWrap invocation, porcelain parsing, concurrency gate |
| `GitDelta.Diff` | Patch parsing, `FileDiff`, row projection, syntax, intra-line differ |
| `GitDelta.GitHub` | GraphQL client, accounts, pull request inbox |
| `GitDelta.Persistence` | OS token stores, SQLite durable user data / outbox / cache |
| `GitDelta.Review` | PR session orchestration, comments, `IReviewTree` |
| `GitDelta.App` | Avalonia UI, custom diff control, DI composition root |
| `GitDelta.AppHost` | Dev-only Aspire host for reviewing OTLP traces/metrics |

Phase 3 AI reads revision-pinned trees via `IReviewTree` and overlays results through `IDiffAnnotationSource` / `IAIReviewService` — never mutating cached `FileDiff`s.

## License

Apache 2.0. See [LICENSE](LICENSE).

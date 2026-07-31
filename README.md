# CodeReviewr

A fast, cross-platform Git client focused on code review.

Built with .NET 10 and Avalonia 12. Phase 1 is a local Git client with a purpose-built virtualized diff viewer. See [Plan.md](Plan-Phase1.md) for the full design.

## Requirements

- **Git 2.30 or later, installed and available on the PATH.** CodeReviewr does not bundle Git; it drives the `git` command line directly so that credential helpers, hooks, `.gitattributes` filters (including Git LFS), and `core.fsmonitor` all behave exactly as they do in your terminal. If Git cannot be found, CodeReviewr will tell you at startup rather than failing part-way through an operation. The Git executable path can be overridden in settings for non-standard installations.
- Windows or macOS. Builds are self-contained, so no .NET runtime install is needed for published artifacts. Development requires the .NET 10 SDK.

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
dotnet restore src/CodeReviewr.slnx
dotnet build src/CodeReviewr.slnx
dotnet test src/CodeReviewr.slnx
dotnet run --project src/CodeReviewr.App
```

### Solution layout

| Project | Role |
| --- | --- |
| `CodeReviewr.Core` | Domain model, settings, abstractions |
| `CodeReviewr.Git` | CliWrap invocation, porcelain parsing, concurrency gate |
| `CodeReviewr.Diff` | Patch parsing, `FileDiff`, row projection, intra-line differ |
| `CodeReviewr.App` | Avalonia UI, custom diff control, DI composition root |

## License

Apache 2.0. See [LICENSE](LICENSE).

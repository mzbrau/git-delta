# CodeSift

A fast, cross-platform Git client focused on code review.

CodeSift is in early development. See [Plan.md](Plan.md) for the Phase 1 implementation plan.

## Requirements

- **Git 2.30 or later, installed and available on the PATH.** CodeSift does not bundle Git; it drives the `git` command line directly so that credential helpers, hooks, `.gitattributes` filters (including Git LFS), and `core.fsmonitor` all behave exactly as they do in your terminal. If Git cannot be found, CodeSift will tell you at startup rather than failing part-way through an operation. The Git executable path can be overridden in settings for non-standard installations.
- Windows or macOS. Builds are self-contained, so no .NET runtime install is needed.

### Installing Git

| Platform | Command |
| --- | --- |
| Windows | `winget install --id Git.Git`, or download from [git-scm.com](https://git-scm.com/download/win) |
| macOS | `brew install git`, or install the Xcode Command Line Tools |

Verify with:

```bash
git --version
```

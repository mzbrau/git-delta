# CodeSift

A fast, cross-platform Git client focused on code review.

<<<<<<< HEAD
CodeSift is in early development. See [Plan.md](Plan.md) for the Phase 1 implementation plan.
=======
CodeSift is in early development. See [Plan](Plan) for the Phase 1 implementation plan.
>>>>>>> 0490caed870fe2ca59f5e84c7ba6d52ab5bb6a5b

## Requirements

- **Git 2.30 or later, installed and available on the PATH.** CodeSift does not bundle Git; it drives the `git` command line directly so that credential helpers, hooks, `.gitattributes` filters (including Git LFS), and `core.fsmonitor` all behave exactly as they do in your terminal. If Git cannot be found, CodeSift will tell you at startup rather than failing part-way through an operation. The Git executable path can be overridden in settings for non-standard installations.
<<<<<<< HEAD
- Windows or macOS. Builds are self-contained, so no .NET runtime install is needed.
=======
- .NET 10 runtime (bundled in packaged builds).
>>>>>>> 0490caed870fe2ca59f5e84c7ba6d52ab5bb6a5b

### Installing Git

| Platform | Command |
| --- | --- |
<<<<<<< HEAD
| Windows | `winget install --id Git.Git`, or download from [git-scm.com](https://git-scm.com/download/win) |
| macOS | `brew install git`, or install the Xcode Command Line Tools |
=======
| macOS | `brew install git`, or install the Xcode Command Line Tools |
| Windows | `winget install --id Git.Git`, or download from [git-scm.com](https://git-scm.com/download/win) |
| Debian/Ubuntu | `sudo apt install git` |
| Fedora | `sudo dnf install git` |
| Arch | `sudo pacman -S git` |
>>>>>>> 0490caed870fe2ca59f5e84c7ba6d52ab5bb6a5b

Verify with:

```bash
git --version
```

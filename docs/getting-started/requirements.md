---
title: Requirements
---

# Requirements

Before you install GIT DELTA, make sure your machine meets these basics.

## Platforms

| Platform | Support |
| --- | --- |
| Windows (x64, arm64) | Supported |
| macOS (Apple Silicon and Intel) | Supported |
| Linux | Not supported |

## Git

GIT DELTA **does not bundle Git**. It runs the `git` command on your machine so that:

- Credential helpers work as they do in your terminal
- Hooks and `.gitattributes` (including Git LFS) behave normally
- Tools like `core.fsmonitor` can speed up large repos

**Minimum version:** Git **2.30** or later, available on your `PATH`.

### Install Git

| Platform | Suggested command |
| --- | --- |
| Windows | `winget install --id Git.Git` — or download from [git-scm.com](https://git-scm.com/download/win) |
| macOS | `brew install git` — or install the Xcode Command Line Tools |

### Verify

```bash
git --version
```

You should see a version of **2.30** or newer.

:::tip
If Git is installed in a non-standard location, you can point GIT DELTA at it later in **Settings → Git**.
:::

## Runtime

Published installers are **self-contained**. You do not need to install the .NET runtime to run a release build.

Building from source requires the **.NET 10 SDK**. See the [project README](https://github.com/mzbrau/git-delta#development) for developer commands.

## Next

- [Install on Windows](./install-windows.md)
- [Install on macOS](./install-macos.md)

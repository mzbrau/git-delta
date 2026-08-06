---
title: Install on macOS
---

# Install on macOS

## 1. Install Git

Prefer Homebrew:

```bash
brew install git
```

Or install the **Xcode Command Line Tools**, which include Git:

```bash
xcode-select --install
```

Confirm:

```bash
git --version
```

You need **2.30** or later.

## 2. Get GIT DELTA

### From Releases

1. Open [GitHub Releases](https://github.com/mzbrau/git-delta/releases)
2. Download the latest macOS build for your architecture when available (`osx-arm64` or `osx-x64`)
3. Open or install the downloaded app

:::note Gatekeeper
macOS may warn about an unsigned or unidentified developer build. If you trust the release from this project’s GitHub Releases page, use **System Settings → Privacy & Security** (or right-click → Open) to allow it.
:::

### From source

If a packaged macOS build is not what you need, run from source with the .NET 10 SDK:

```bash
dotnet restore src/GitDelta.slnx
dotnet run --project src/GitDelta.App
```

See the [project README](https://github.com/mzbrau/git-delta#development).

## 3. Launch

- If Git cannot be found (or is too old), GIT DELTA shows a blocking window with Homebrew-oriented hints.
- Otherwise you see the welcome screen — continue with [First launch](./first-launch.md).

## Credentials

Push and pull use your normal Git credential setup (for example **osxkeychain** or SSH agent). GIT DELTA does not replace those helpers for local remotes.

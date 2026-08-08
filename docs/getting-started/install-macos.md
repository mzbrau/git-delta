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

Published macOS builds target **Apple Silicon** (`osx-arm64`) and ship as an unsigned Velopack portable zip.

1. Open [GitHub Releases](https://github.com/mzbrau/git-delta/releases)
2. Download `GitDelta-osx-Portable.zip`
3. Unzip the archive — you should get `GIT DELTA.app`
4. Drag the app to **Applications**, or run it in place
5. Open the app (see Gatekeeper note below if macOS blocks it)

Intel Macs (`osx-x64`) and a signed `.pkg` installer are not published yet; use [From source](#from-source) on those machines.

:::note Gatekeeper
macOS builds are **not code-signed or notarized** yet. Gatekeeper may block the first launch. If you trust the release from this project’s GitHub Releases page, right-click the app → **Open**, or allow it under **System Settings → Privacy & Security**.
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

---
title: Install on Windows
---

# Install on Windows

## 1. Install Git

If Git is not already installed:

```powershell
winget install --id Git.Git
```

Or download the installer from [git-scm.com](https://git-scm.com/download/win).

Confirm:

```powershell
git --version
```

## 2. Download GIT DELTA

1. Open [GitHub Releases](https://github.com/mzbrau/git-delta/releases)
2. Download the latest Windows **setup** executable (`win-x64`)
3. Run the installer

Installers are packaged with Velopack. Running a newer Setup.exe upgrades an existing install.

## 3. Launch

Start **GIT DELTA** from the Start menu or desktop shortcut.

- If Git is missing or too old, you will see a blocking message with install hints — fix Git first, then reopen the app.
- If Git is fine, you land on the welcome screen. Continue with [First launch](./first-launch.md).

## Building from source (optional)

For development builds:

```bash
dotnet restore src/GitDelta.slnx
dotnet run --project src/GitDelta.App
```

Details are in the [project README](https://github.com/mzbrau/git-delta#development).

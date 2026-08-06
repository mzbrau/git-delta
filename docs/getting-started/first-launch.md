---
title: First launch
---

# First launch

## If Git is missing

{/* Capture: Git missing / too-old blocking window  */}
![Git missing window](/img/placeholders/git-missing.png)

GIT DELTA checks for Git at startup.

- If Git is **missing** or **older than 2.30**, a blocking window explains how to install it.
- Use the platform hints (for example `winget` on Windows or `brew` on macOS), then restart the app.

You cannot continue until Git is available.

## Welcome screen

{/* Capture: Welcome / no-repo state with Open Repository and recent list  */}
![Welcome screen](/img/placeholders/welcome-screen.png)

When no repository is open you see:

- A short prompt to open a local Git repository
- **Open Repository…** — pick a folder that contains a `.git` directory
- **RECENT** — reopen a repository you used before

### Try this

1. Click **Open Repository…**
2. Choose an existing clone
3. Confirm the window switches into the main workspace (toolbar + sidebar)

## Theme

On first use, the theme follows **Settings → General**:

| Option | Behavior |
| --- | --- |
| System | Match OS light/dark |
| Light | Always light |
| Dark | Always dark |

Change it anytime from the settings gear in the toolbar.

## Development folder (optional but useful)

In **Settings → General**, set a **Development folder**.

GIT DELTA can scan that tree for Git repositories and list them in the sidebar catalog — handy when you jump between many clones (including ones used for pull request review).

## Next

- [Main window](../tour/main-window.md)
- [Navigation](../tour/navigation.md)
- [Working copy](../local/working-copy.md)

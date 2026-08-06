---
title: Branches and remotes
---

# Branches and remotes

## Switch branches

1. Expand **Branches** in the sidebar
2. Right-click (or open the context menu on) the branch you want
3. Choose **Check Out**

The toolbar shows the current branch name for awareness. Checkout is done from the sidebar list.

## Remote operations

Use the toolbar:

| Action | Typical use |
| --- | --- |
| **Fetch** | Update remote-tracking refs without merging |
| **Pull** | Bring remote commits into your current branch |
| **Push** | Publish local commits |

While a remote operation runs, the matching controls show a busy state and may be disabled.

Authentication uses your normal Git credential helpers (Git Credential Manager, osxkeychain, SSH agent, and so on).

## View Remote

**View Remote** opens the configured remote URL in your default browser — useful for jumping to the hosting site.

## Reveal in file manager

| Platform | Label |
| --- | --- |
| macOS | **Show in Finder** |
| Windows | **Show in Explorer** |

This reveals the repository folder in the OS file manager.

## Tips

- Prefer **Fetch** then inspect **History** before a large pull
- If push/pull fails, expand **Git Output** and check [Troubleshooting](../reference/troubleshooting.md)

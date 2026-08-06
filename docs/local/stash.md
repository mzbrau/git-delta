---
title: Stash
---

# Stash

Stashes let you set aside work temporarily and come back to it later.

## Stash dialog

{/* Capture: Stash push/pop dialog  */}
![Stash dialog](/img/placeholders/stash-dialog.png)

Open it from the toolbar **Stash** button.

| Action | Purpose |
| --- | --- |
| **Push** | Save current changes to a new stash (optional message) |
| **Pop** | Apply the latest stash and remove it from the stash list |
| Include untracked | When pushing, also stash untracked files |

:::tip
Including untracked files is easy to forget. Enable it when your new files should travel with the stash.
:::

## Sidebar stashes

Under **STASHES**:

1. Select a stash to see its message, ref, related branch, and files
2. Use the context menu:
   - **Apply** — apply without necessarily dropping (per command semantics in the UI)
   - **Delete** — drop the stash

## Example: switch branches with dirty work

1. Click **Stash** → **Push** with a short message
2. Check out the other branch from **Branches**
3. Do your work and commit
4. Return to your branch and **Pop** or **Apply** the stash from the sidebar

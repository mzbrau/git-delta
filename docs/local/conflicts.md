---
title: Conflicts
---

# Conflicts and in-progress Git state

When a merge, rebase, or similar operation is in progress, GIT DELTA surfaces that state clearly.

![In-progress banner](../img/in-progress-banner.png)

## In-progress banner

The banner shows a short message for the current state and actions such as:

| Action | Typical effect |
| --- | --- |
| **Abort** | Cancel the in-progress operation and restore the previous state |
| **Continue** | Continue after you have resolved what Git expects |

During an interactive [rebase](./rebase.md), the wizard may offer **Resume** / **Abort** on its conflict step.

## Conflicted files list

On **File Status**, conflicted paths appear under **CONFLICTED**.

Today this list is primarily for **visibility**:

- You can see which paths need attention
- Context actions focus on revealing the file in the file manager

Opening an external mergetool or marking paths resolved from dedicated buttons is not exposed in the UI yet — resolve conflicts with your usual editor/mergetool, then **Continue** / **Resume** in GIT DELTA.

## Practical workflow

1. Notice the banner — do not ignore an in-progress state
2. Open **File Status** and inspect **CONFLICTED**
3. Resolve files in your editor or mergetool
4. Stage resolutions as Git requires
5. Click **Continue** (or **Resume** in the rebase wizard)

If something fails, expand **Git Output** and see [Troubleshooting](../reference/troubleshooting.md).

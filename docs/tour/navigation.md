---
title: Navigation
---

# Navigation

The left sidebar is how you move between repositories and workspace modes.

{/* Capture: Sidebar showing File Status, History, Branches, Stashes, PRs  */}
![Sidebar navigation](/img/placeholders/sidebar-navigation.png)

## Repository switcher

| Feature | Description |
| --- | --- |
| Collapse / expand | Hide or show the sidebar |
| **Open repository…** | Pick another local clone |
| Current repo | Name and subtitle for the active repository |
| Filter | Narrow the catalog list |
| Catalog list | Repositories discovered under your Development folder |

Set **Settings → General → Development folder** so the catalog can scan for clones.

**Repository Settings** opens Settings (useful for accounts when working with pull requests).

## WORKSPACE

| Item | Opens |
| --- | --- |
| **File Status** | Working copy — staged/unstaged changes (badge shows change count) |
| **History** | Commit history for the repo |

## BRANCHES

- Expand to see local branches
- Use the context menu **Check Out** to switch branches
- The current branch is indicated in the list

:::tip
Checkout lives here. The toolbar branch label is informational.
:::

## STASHES

- Select a stash to see its message, ref, and files
- Context menu: **Apply** or **Delete**

See also [Stash](../local/stash.md).

## PULL REQUESTS

When a GitHub account is connected, the sidebar shows inbox sections such as:

- Needs my review
- Reviewed
- My pull requests

Opening a PR switches into the pull request review workspace. See [Inbox](../review/inbox.md).

If an account needs re-authentication, a banner links you to Settings.

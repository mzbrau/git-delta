---
title: History
---

# History

**History** browses commits in the current repository.

{/* Capture: History commit list + details + file diff  */}
![History view](/img/placeholders/history-view.png)

## Commit list

Each row typically shows:

- Subject
- Author
- Date
- Decorations (branch / tag indicators where available)

### Find commits

| Control | Purpose |
| --- | --- |
| Branch switcher | Limit history to a branch |
| Filter / search | Narrow the list |
| **Load more** | Fetch an older page of history |

## Context menu

| Action | Effect |
| --- | --- |
| **Checkout** | Check out that commit |
| **Cherry Pick** | Apply the commit onto the current branch |
| **Reverse Commit** | Create a revert of the commit |
| **Copy Hash** | Copy the object id |
| **Copy Message** | Copy the commit message |

## Commit details

Selecting a commit shows:

- Subject and body
- Object id, author, date
- Labels / decorations
- Files changed in that commit (flat or tree, with filter/search)

Select a file to open its diff in the [diff viewer](./diff-viewer.md) (read-only — no staging).

## Example: inspect a merge

1. Open **History**
2. Search for the merge subject or author
3. Select the commit
4. Walk the file list and review each diff side-by-side

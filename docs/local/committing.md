---
title: Committing
---

# Committing

The commit dock sits with **File Status** so you can stage and commit without changing screens.

{/* Capture: Commit message dock (amend / no-verify / push options)  */}
![Commit dock](/img/placeholders/commit-dock.png)

## Write a message

1. Stage the changes you want included
2. Enter a subject (and optional body) in the message box
3. Click the commit button (label may vary with options)

### Helpers

| Helper | What it does |
| --- | --- |
| **Recent messages** | Flyout of recent commit messages — pick one to reuse |
| **Ticket from branch** | Inserts a ticket id parsed from the branch name (regex in **Settings → Git**) |
| **AI generate commit message** | When AI assist is enabled, drafts a message from the staged changes |

## Commit options

| Option | Meaning |
| --- | --- |
| **Amend** | Amend the previous commit (use carefully on shared branches) |
| **--no-verify** | Skip Git hooks for this commit |
| **Push** after commit | Push once the commit succeeds |

Hook output appears in the UI when hooks run so you can see failures without digging into a terminal.

## Magic Commit

{/* Capture: Magic Commit overlay  */}
![Magic Commit](/img/placeholders/magic-commit.png)

**Magic Commit** helps split work into logical commits:

1. Open Magic Commit from the commit assist controls
2. Optionally add instructions
3. Choose staged-only vs all pending changes
4. Review the proposed split and results

Use it when a large working tree should become several focused commits.

## Example: commit and push

1. Stage files for one logical change
2. Write a clear subject line
3. Enable **Push** after commit if you want the remote updated immediately
4. Commit and watch status / Git Output if something fails

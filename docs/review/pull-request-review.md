---
title: Pull request review
---

# Pull request review

The PR workspace is built for reading diffs and leaving high-quality review comments.

{/* Capture: PR review: file list + diff + conversation  */}
![PR review](/img/placeholders/pr-review.png)

## Header actions

| Action | Purpose |
| --- | --- |
| **Open on GitHub** | View the PR in the browser |
| Reviewer badges | Who is assigned / reviewing |
| **Submit** | Upload pending comments / submit a review (badge shows pending count) |
| **Request changes / Approve** | Submit with that decision (via the submit flow) |
| Mark as not viewed | Clear viewed state for navigation |
| Begin file comment | Start a file-level comment |
| AI review | Optional assist — see [AI assist](./ai-assist.md) |

## File list

| Capability | Notes |
| --- | --- |
| Flat / tree | Same idea as the working copy |
| Filter / search | Focus the list; shortcuts can focus the filter |
| Filters | All, Viewed, Not viewed, Stale, Commented, Unresolved |
| Viewed indicator | Track what you have already read |
| Private notes | Local-only notes that are never synced to GitHub |
| Conversation / Change Briefing rows | Jump into discussion or AI briefing |

### Keyboard (PR mode)

See the full list in [Keyboard shortcuts](../reference/keyboard-shortcuts.md). Highlights:

| Shortcut | Action |
| --- | --- |
| **J** / **K** or arrows | Next / previous file |
| **V** | Toggle viewed |
| **N** / **P** | Next / previous thread |
| **C** | Focus comment draft |
| **Ctrl+Enter** | Submit pending review |

:::note
These shortcuts use **Control** even on macOS (except where noted elsewhere for copy).
:::

## Conversation

The conversation view can include:

- PR description / summary
- Checks
- Timeline
- Reviewers and status

## Comments on the diff

| Feature | Description |
| --- | --- |
| Line comments | Comment on a line or range |
| Threads | Reply, resolve / unresolve |
| Suggestions | Add a suggestion block where supported |
| Mentions | `@` completion with keyboard navigation |
| Pending | Not yet synced to GitHub until you submit |

### Submit vs draft

- Draft comments stay local until you **Submit**
- Submit can include a summary and a decision (comment / approve / request changes)

## Offline drafts

Previously opened PRs and pending outbox items are designed so you can keep reviewing when the network blips — submit when you are back online.

## Local notes

Use the local notes scratchpad for personal markdown that stays on your machine (never synced as a GitHub review comment).

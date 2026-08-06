---
title: Why GIT DELTA
---

# Why GIT DELTA

GIT DELTA exists for a simple reason: **reviewing code well is harder than ever — and more important than ever.**

## Review in the age of AI

AI coding tools write more of the code that lands in pull requests and local working trees.

That often means:

- Senior developers review code they did not write
- Changes span unfamiliar areas of a rapidly changing codebase
- Volume goes up while familiarity goes down

In that world, speed alone is not enough. You need a clear overview, strong diffs, and optional AI that **helps you understand** — without outsourcing the review.

### Human review still matters

For mission-critical and enterprise software, human reviewers remain essential:

- Catch design and maintainability issues AI misses
- Keep systems understandable over years, not just “green” in CI
- Own accountability for what ships

GIT DELTA is built so you can finish local reviews and pull requests **faster**, while keeping that human judgment in the loop.

## AI as an assistant — not a replacement

When AI assist is enabled, GIT DELTA can:

- Summarize changes and highlight risk
- Suggest a review order
- Answer questions about a file or selection

It does **not**:

- Auto-approve or reject reviews
- Submit comments or reviews for you
- Modify your files

You stay in control. See [AI assist](./review/ai-assist.md) for details.

## Performance when Git feels slow

Enterprise machines often make file operations expensive:

- Antivirus and anti-malware scanning
- Disk encryption
- Network or virtualized drives

Those factors make `git status` and `git diff` feel sluggish — and waiting on every click adds friction.

GIT DELTA is designed so the UI stays responsive:

- Git work runs asynchronously
- Diffs and status are prepared in the background where possible
- Prefetch aims to have the next file ready when you select it

If a Git command takes several seconds, the app should still feel usable. Learn more in [Performance](./reference/performance.md).

## Design principles (in plain language)

| Principle | What it means for you |
| --- | --- |
| **Performance first** | The app should feel instant even when Git is not |
| **Review first** | Large diffs, simple file navigation, minimal clutter |
| **Native desktop** | Keyboard-friendly, themes, window state that sticks |
| **Human first (AI)** | Advisory help only; you decide what to accept and submit |

## Next steps

- [Requirements](./getting-started/requirements.md)
- [Install on Windows](./getting-started/install-windows.md) or [macOS](./getting-started/install-macos.md)
- [First launch](./getting-started/first-launch.md)

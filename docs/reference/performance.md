---
title: Performance
---

# Performance

Performance is a core feature of GIT DELTA — especially on machines where Git feels slow.

## Why enterprise disks feel slow

On many corporate laptops, every file touch is expensive:

- Antivirus / anti-malware scanning
- Full-disk encryption
- Virtualization or network-backed home directories

That makes `git status`, `git diff`, and related commands take longer than on a personal machine. Waiting on every click turns review into friction.

## How GIT DELTA helps

| Technique | What you notice |
| --- | --- |
| Async Git | The UI stays interactive while commands run |
| Background preparation | Status and diffs can be ready before you ask |
| Diff prefetch | Selecting the next file often feels instant (look for the cached indicator) |
| Progressive loading | Large repos remain usable |
| Cancellation | In-flight work can be abandoned when you move on |

Goal: if a Git command takes several seconds, the application should still feel responsive.

## Diagnostics

In **Settings → Diagnostics**:

| Tool | Use |
| --- | --- |
| **Simulate slow Git** | Artificially delay operations to verify the UI stays usable |
| Prefetch tunables | Adjust how aggressively diffs are prepared |
| Timing summary | Inspect measured timings |

## Tips for large repositories

- Keep a sensible **Development folder** scan root (avoid scanning the entire disk)
- Prefer reviewing with prefetch warm — click through files without thrashing filters constantly
- Use ignore-whitespace and focused filters to reduce visual and cognitive load
- Consider enabling Git’s own `fsmonitor` / untracked cache in your environment when appropriate (Git-side; GIT DELTA benefits from a faster `git`)

## Related

- [Why GIT DELTA](../why-git-delta.md)
- [Working copy](../local/working-copy.md)
- [Settings](./settings.md)

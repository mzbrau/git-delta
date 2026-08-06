---
title: Troubleshooting
---

# Troubleshooting

## Git missing or too old

**Symptom:** Blocking window at startup; app will not open a repo.

**Fix:**

1. Install Git 2.30+ ([Requirements](../getting-started/requirements.md))
2. Confirm `git --version` in a terminal
3. Restart GIT DELTA
4. If Git is non-standard, check **Settings → Git** for the resolved path

## Authentication failures (push / pull / fetch)

**Symptom:** Remote operation fails; toast or Git Output shows auth errors.

**Fix:**

- Ensure Git Credential Manager / osxkeychain / SSH agent works in your terminal for the same remote
- For SSH, load your key into the agent if it is passphrase-protected
- Retry from the toolbar after fixing credentials

## Pull request inbox empty or errors

**Check:**

1. **Settings → Accounts** — valid PAT for the right host
2. Token permissions / org restrictions (especially on Enterprise)
3. Re-auth banner → update the token
4. Network access to github.com or your GHES host

## Cannot find the local clone for a PR

**Fix:** Set **Settings → General → Development folder** to a parent of your clones, then wait for / trigger a scan so the catalog can discover the repository.

## AI connection problems

**Check:**

1. AI enabled and disclosure accepted (**Settings → AI**)
2. Copilot token valid
3. **Test connection**
4. Path denylist / excluded repos not blocking the repo you expect
5. Budgets / timeouts not set impossibly low

## Diff looks wrong or empty

**Try:**

- **Refresh** on the diff toolbar
- Confirm you selected the intended list (staged vs unstaged)
- Toggle whitespace / full file
- Expand **Git Output** for underlying command errors

## Where to look for details

| Place | What it shows |
| --- | --- |
| Toasts | Short errors with Retry / Undo when available |
| In-progress banner | Merge/rebase state |
| **Git Output** | Streamed git stdout/stderr — best first stop for hard failures |
| Settings → Diagnostics | Timing and slow-Git simulation |

## Still stuck?

Open an issue on [GitHub](https://github.com/mzbrau/git-delta/issues) with:

- Platform (Windows/macOS)
- `git --version`
- What you clicked
- Relevant **Git Output** (redact secrets)

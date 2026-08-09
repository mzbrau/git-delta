---
title: Settings
---

# Settings

Open Settings from the toolbar gear.

![Settings General](../img/settings-general.png)

## General

| Setting | Description |
| --- | --- |
| Development folder | Root folder scanned for Git repositories |
| Theme | System, Light, or Dark |
| Open Repository… | Open a clone from Settings |

## Accounts

| Setting | Description |
| --- | --- |
| Add GitHub account | Host + PAT → OS keychain / credential manager |
| Enterprise hosts | GitHub Enterprise Server URLs |
| Remove / re-auth | Manage existing accounts |

See [GitHub accounts](../review/github-accounts.md).

## Diff

| Setting | Description |
| --- | --- |
| Default view | Side by side or Unified |
| Ignore whitespace | Default whitespace handling |
| Context lines | Default context around hunks |

## Shortcuts

| Setting | Description |
| --- | --- |
| Keyboard shortcuts | View and remap application shortcuts |
| Change | Capture the next key chord for a binding |
| Reset / Clear | Restore the default gesture or unbind |
| Reset all to defaults | Restore every binding |

Gestures use `Ctrl+…` on Windows and macOS. Diff viewer copy and AI chat Enter keys remain fixed. See [Keyboard shortcuts](./keyboard-shortcuts.md) for the default table and id names used in `settings.json`.

## Git

| Setting | Description |
| --- | --- |
| Detected Git path / version | Read-only diagnostics of the resolved executable |
| Ticket-from-branch regex | Pattern used to insert ticket ids into commit messages |

## AI

| Setting | Description |
| --- | --- |
| Enable + disclosure | Turn on AI assist after acknowledging data handling |
| Copilot token | Dedicated token for AI features |
| Model / reasoning effort | Model selection and effort |
| Rules | Guidance text for AI behavior |
| Budgets / timeouts | Bound cost and duration |
| Briefing thresholds | When briefings kick in |
| Export retention | How long exported/materialized AI data is kept |
| Large-PR threshold | Treat large PRs carefully |
| Path denylist | Paths excluded from AI |
| Excluded repos | Repositories opted out of AI |
| Test connection / Refresh models | Setup helpers |
| Clear all AI data | Wipe stored AI artifacts |

See [AI assist](../review/ai-assist.md).

## Diagnostics

| Setting | Description |
| --- | --- |
| Simulate slow Git | Artificially delay Git ops to test responsiveness |
| Diff prefetch tunables | Adjust background diff preparation |
| Timing summary | Performance timing information |

See [Performance](./performance.md).

# Screenshot placeholders

Capture screenshots from GIT DELTA and save them under this folder using the filenames below.

Each docs page that needs a visual includes:

```markdown
{/* Capture: description of what to show */}
![Alt text](/img/placeholders/filename.png)
```

## Checklist

| Filename | Capture notes |
| --- | --- |
| `welcome-screen.png` | Welcome / no-repo state with Open Repository and recent list |
| `git-missing.png` | Git missing / too-old blocking window |
| `main-window-overview.png` | Full main window: toolbar, sidebar, file list, diff |
| `sidebar-navigation.png` | Sidebar showing File Status, History, Branches, Stashes, PRs |
| `file-status-overview.png` | File Status with staged + unstaged lists |
| `diff-side-by-side.png` | Side-by-side diff with hunk actions visible |
| `diff-unified.png` | Unified diff view |
| `commit-dock.png` | Commit message dock (amend / no-verify / push options) |
| `magic-commit.png` | Magic Commit overlay |
| `history-view.png` | History commit list + details + file diff |
| `stash-dialog.png` | Stash push/pop dialog |
| `rebase-wizard.png` | Rebase wizard (plan editing step) |
| `in-progress-banner.png` | Merge/rebase in-progress banner with Abort/Continue |
| `settings-general.png` | Settings → General |
| `settings-accounts.png` | Settings → Accounts |
| `settings-ai.png` | Settings → AI |
| `pr-inbox.png` | Pull Requests inbox sections |
| `pr-review.png` | PR review: file list + diff + conversation |
| `ai-review-panel.png` | AI review / Change Briefing side panel |
| `local-pending-review.png` | Local pending-changes review (briefing + comments) |

After capture, replace the gray stand-in PNGs in this folder with real screenshots. Prefer PNG at 2x where practical.

Gray placeholder files are committed so the docs site builds before screenshots exist.

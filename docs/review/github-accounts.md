---
title: GitHub accounts
---

# GitHub accounts

Pull request review uses a **personal access token (PAT)** stored in your OS keychain or credential manager — not a shared OAuth app flow.

{/* Capture: Settings → Accounts  */}
![Settings Accounts](/img/placeholders/settings-accounts.png)

## Add an account

1. Open **Settings** (toolbar gear)
2. Go to **Accounts**
3. Add a GitHub account:
   - Host (github.com or your Enterprise Server URL)
   - Personal access token
4. Save — the token is stored by the OS secret store

You can add more than one account (for example github.com plus a GHES host).

## Token tips

| Topic | Guidance |
| --- | --- |
| Scopes | Enough to read PRs, post reviews/comments, and mark files viewed as needed by your org |
| Enterprise | Many orgs require classic PATs or admin-approved token types — follow your IT policy |
| Re-auth | If a banner says the account needs re-authentication, return here and update the token |

## Development folder

For PR workflows, set **Settings → General → Development folder** to a root that contains your clones.

GIT DELTA discovers repositories under that folder so opening a pull request can switch to the matching local clone without hunting manually.

## Next

- [Inbox](./inbox.md)
- [Pull request review](./pull-request-review.md)

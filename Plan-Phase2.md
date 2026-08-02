# CodeReviewr - Phase 2 Implementation Plan

> **Objective:** Turn CodeReviewr from a high-performance local Git client into a dedicated GitHub pull request review application.
>
> Phase 2 introduces GitHub integration while preserving the performance-first philosophy established in Phase 1.
>
> The review experience should be measurably better than the GitHub web interface while remaining familiar to GitHub users.
>
> AI is deliberately not part of this phase. Everything built here is the foundation Phase 3 stands on.

---

# Goals

By the end of Phase 2 a developer can:

- Authenticate against GitHub.com and GitHub Enterprise Server
- See every pull request awaiting their review, across every repository
- See their own open pull requests
- Read the description, the conversation, and the check status without leaving the application
- Review the code in the Phase 1 diff viewer
- Navigate changed files by keyboard
- Read, write, reply to, resolve and unresolve review comments
- Submit a review as Approve, Comment, or Request Changes
- Keep reviewing when GitHub is unreachable
- Resume a review later
- Never disturb their own uncommitted work

---

# Design Principles

## Review First

Everything optimises for reading code.

GitHub is a metadata provider. The desktop application is the review experience.

## Performance First

The Phase 1 rules carry forward unchanged.

- Never block the UI thread.
- Never wait on a GitHub request that a local read could satisfy.
- Everything loads progressively.
- Background work is cancellable.
- Cache aggressively, and content address whatever can be content addressed.
- Synchronise only when there is a reason to.

Phase 2 adds one rule, because the network is new:

- A network failure degrades the experience. It never blocks it.

## Local First

Every diff is generated locally, from the object database, by the Phase 1 diff engine.

No GitHub-rendered diff, patch, or `diff_hunk` text ever reaches the screen. This is absolute, and it has consequences elsewhere in this document: it is why a pull request cannot be reviewed without a local clone, and it is why an outdated comment's context is regenerated from local blobs rather than displayed from the snippet GitHub supplies.

Benefits:

- Consistent rendering with local review
- Syntax highlighting and intra-line refinement from one engine
- Diffs that cost nothing to re-render
- The foundation Phase 3's AI reads from

---

# Relationship to Phase 1

## Corrections to the Phase 1 roadmap

Phase 1 listed "History" as a Phase 2 placeholder. History is built. It leaves Phase 2's scope.

Phase 1 listed "Live updates" under the Phase 2 feature list. Phase 2 rejects live updates. See Synchronisation.

## Phase 1 remainder

Two Phase 1 exit criteria are unmet, and both matter more in pull request review than in local review, because a reviewer is reading unfamiliar code rather than code they just wrote:

- `ISyntaxTokenService` is implemented but referenced by nothing in `App`. Diffs render unhighlighted.
- Intra-line spans are computed on `DiffLine` and `DiffRow`, but `DiffViewer` paints one brush per line, so word-level diff is invisible.
- Verify is referenced by `CodeReviewr.Diff.Tests` but no snapshots are pinned, so Phase 1's M2 exit criterion on row projection and token span snapshots was never met.

These are Phase 2's M0. Phase 2 cannot claim to reuse a diff engine with syntax highlighting and word diff until they are wired.

## What Phase 2 inherits unchanged

| Component | Reused as-is |
| --- | --- |
| `IGitProcessRunner` and CliWrap invocation | Yes |
| `IGitObjectReader` (`cat-file --batch`) | Yes, and it becomes load bearing |
| `PatchParser`, `FileDiff`, row projectors | Yes |
| `DiffViewer` | Yes |
| `MemoryDiffCache`, content addressing by `ContentId` | Yes |
| `IRepositoryGate` | Yes, with a keying change |
| `DiffAnchor` and the annotation overlay | Yes. This is what Phase 1 designed it for |

---

# Application Scope

## One inbox, many repositories

A review inbox is inherently cross-repository. `review-requested:@me` spans every repository the user can see, and the Development Folder concept only makes sense if the inbox is not scoped to whatever happens to be open.

Phase 2 therefore separates two things Phase 1 conflated:

| Concern | Scope |
| --- | --- |
| Pull request inbox | Application-level, spans all accounts and all repositories |
| Active repository context | Exactly one at a time, as in Phase 1 |

Opening a pull request switches the active repository context to that pull request's repository.

Accepted cost: the user cannot view their local working copy of one repository beside a review of another. Returning to local work is an explicit switch. Full multi-repository contexts are deferred; nothing in this design blocks them later.

## Gate keying

`IRepositoryGate` and `GitRepositoryWatcher` remain singletons, but are keyed by **git common directory** rather than by repository path.

The index lock is a property of the common directory, not of a checkout. Keying by path would let two contexts pointing at the same underlying repository take the same lock tier concurrently, which is exactly the failure the gate exists to prevent.

---

# GitHub Integration

## Supported instances

- GitHub.com
- GitHub Enterprise Server

## Authentication

**Personal access tokens only.** No OAuth.

The user pastes a token. It is validated immediately with a `viewer` query, which also supplies the login and avatar. That is the whole flow.

### Why OAuth is rejected

GitHub's OAuth **web flow requires a client secret**, and a desktop application cannot hold one. Shipping it anyway means an extractable secret that is revoked for every user simultaneously the first time someone pulls it out of a binary.

**Device flow** does avoid the secret, and it is the correct answer for a desktop client on GitHub.com. It is rejected here for Enterprise: there is no client ID that works across GHES instances, because each instance needs its own OAuth application registered by an administrator. On GHES, OAuth is admin-gated by construction, and a token is the only path a user can walk alone.

Supporting both would mean two authentication paths, one of which is unavailable to a meaningful share of the target users. Supporting only the one that always works is smaller and never leaves a user stuck.

This is also consistent with Phase 1, which delegated credentials rather than building a credential system.

Accepted cost: an unfriendly first run. The user visits GitHub, creates a token, and pastes it. Onboarding must make the required scopes unmissable rather than leaving the user to guess.

### Scopes

Documented explicitly in onboarding, not left to the user:

| Token type | Required |
| --- | --- |
| Fine-grained | Pull requests: read and write. Contents: read. Metadata: read. |
| Classic | `repo` |

Fine-grained tokens are genuinely less privileged and should be recommended. **Many organisations disable them**, so in practice enterprise users fall back to a classic token, at which point Phase 2 holds a credential with full read and write access to every private repository the user can reach. That is a real security statement and the plan states it rather than eliding it.

### Expiry

Fine-grained tokens expire within about a year, and classic tokens can be set to expire. A `401` means "re-authenticate this account", never "something went wrong". The affected account is marked, its inbox sections show a re-authentication prompt in place of an error, and every other account keeps working.

## Token storage

Phase 1's position was that "storing secrets is a security surface Phase 1 deliberately does not take on." Phase 2 takes it on. This is a deliberate reversal, not an oversight.

Tokens live in the operating system keychain behind `ITokenStore`:

| Platform | Mechanism |
| --- | --- |
| macOS | Keychain, via `Security.framework` |
| Windows | Credential Manager, via `CredRead` / `CredWrite` |

No token is ever written to `settings.json`, to logs, or to the SQLite store.

This is cheap specifically because Phase 1 excluded Linux. There is no libsecret, GNOME Keyring, or KWallet fallback chain to build, and no headless case to degrade into.

## Accounts

An account is keyed by `(host, login)`, so two GitHub.com accounts can coexist. A personal account and a work account on the same host is a common arrangement, and keying by host alone would force a sign-out to switch between them.

Each account stores:

- Host URL
- Login
- Avatar URL
- Token reference (the secret itself lives in the keychain)

The inbox is the **union across all accounts**, with the owning account shown on each pull request. A split inbox is not an inbox.

A discovered repository is bound to an account the first time it is used. The binding is remembered and can be changed in settings.

## API surface

**GraphQL is primary.** A single hand-written client, `HttpClient` plus `System.Text.Json` source generation, with query documents as embedded resources.

### Why GraphQL

Two operations Phase 2 requires do not exist in REST at all:

- `resolveReviewThread` and `unresolveReviewThread`. REST does not expose whether a thread is resolved, let alone let you change it.
- `markFileAsViewed` and `unmarkFileAsViewed`, and the `viewerViewedState` field.

Beyond capability, GraphQL is how the progressive-loading budget is met. One query returns the pull request, its changed files, its review threads and their comments, its check status and its review decision. The REST equivalent is four calls plus client-side reconciliation.

### Why the client is hand-written

Octokit.net is mature but cannot express the operations above, so it would cover at most half the surface. Octokit.GraphQL.net is prerelease and would pin a schema version. Taking both means two clients, two authentication paths, and two rate-limit budgets to reason about, while still hand-writing the GraphQL DTOs.

This is the Phase 1 pattern: own the seam when the library cannot express the operation that matters. LibGit2Sharp was rejected for the same reason.

### Rate limits

| Budget | Limit |
| --- | --- |
| GraphQL | 5,000 points per hour, cost derived from node counts |
| REST, if ever used | 5,000 requests per hour |
| REST search | 30 requests per minute |
| Search results | 1,000 maximum, 100 per page |

**GraphQL has no conditional-request mechanism.** REST returns ETags and a `304` costs nothing against the limit; a GraphQL refresh always pays full points and a full response body. This does not make GraphQL the wrong choice, since thread resolution requires it, but it does mean the refresh cadence is conservative by design rather than by preference. See Synchronisation.

### Enterprise schema drift

GitHub Enterprise Server ships an older GraphQL schema than GitHub.com, and a mutation Phase 2 depends on may simply not exist on an older instance.

- Probe capabilities once per account, on first connection, and cache the result.
- A missing capability degrades that feature for that account and states why. It never throws.
- The concrete case to handle is `markFileAsViewed`: where it is absent, viewed state falls back to local-only for that account.

## Git credentials remain Phase 1's

The token authenticates the **API only**. It is never handed to `git`.

Git network operations — fetching pull request refs, cloning — continue to use the user's configured credential helper exactly as in Phase 1. The user has already cloned these repositories with those credentials; nothing needs to change, and injecting a token into `git` invocations would replace a working mechanism with a worse one.

---

# Repository Discovery

The user configures a single **Development Folder**.

```
~/Development
    Personal/
        ToolA
        ToolB
    Work/
        Backend
        Frontend
```

## Scanning

- Descend recursively, **stopping at the first `.git`**. A repository inside a repository is a submodule or a vendored copy. Nobody reviews a pull request against one. The Phase 2 draft's claim that "nested repositories are supported" is withdrawn.
- Skip hidden directories and a standard ignore list: `node_modules`, `bin`, `obj`, `target`, `Pods`, `DerivedData`, `.venv`, `vendor`.
- Bounded depth, default 6, configurable.
- The scan runs in the background and publishes results progressively. It never blocks the inbox.
- Results are cached in SQLite with a TTL, rescanned on manual refresh, and rescanned whenever a match misses.

Without the ignore list, a single JavaScript repository can contribute tens of thousands of directories to the walk, and the failure mode is a scan that never finishes rather than one that finishes wrong.

## Matching

The match key is a remote URL, not a folder name.

- Consider **every remote**, not just `origin`. A contributor's clone has `origin` pointing at their fork and `upstream` at the canonical repository, and the pull request lives on the canonical one.
- Normalise to `host/owner/name`: strip the scheme, resolve `git@` and `ssh://` forms, resolve SSH host aliases from `~/.ssh/config`, strip a trailing `.git`, lowercase.
- Once a repository is matched, **remember the pairing by GitHub node ID**. A repository renamed on GitHub still has the old URL in the local remote and will silently stop matching otherwise.
- When two local clones resolve to the same repository, prompt once and remember the choice.

## When there is no local clone

Reviewing requires local objects, because every diff is generated locally. There is no second, server-rendered path.

The prompt therefore offers **Clone** or **Cancel**. "Review without cloning" is withdrawn from the plan: it would mean either an invisible second copy of the repository somewhere the user did not ask for, or a GitHub-rendered diff, and the second contradicts Local First outright.

The clone is an ordinary full clone with a working copy, placed in the Development Folder. It lands somewhere the user can see it and use it for their own work, which a bare or `--no-checkout` clone would not.

Accepted cost: the first review of a large repository the user has never cloned waits for a full clone. This is honest, progress-bearing, and cancellable, in line with Phase 1's treatment of clone.

---

# No Worktrees

**Phase 2 creates no worktrees.** This is the largest change from the original Phase 2 sketch, and it makes the plan smaller, faster, and safer at the same time.

## The requirement, restated

The user's uncommitted work must never contaminate a review, and must never be disturbed by one.

## Why worktrees are the wrong instrument

Reviewing needs *content at a commit*. It does not need files on disk. Every read the review performs is already expressible against the object database:

| Need | Command | Reads the worktree? |
| --- | --- | --- |
| The diff | `git diff <merge-base>...<head>` | No |
| File content at a revision | `git show <sha>:<path>`, `cat-file --batch` | No |
| File list at a revision | `git ls-tree -r <sha>` | No |
| Search at a revision | `git grep <pattern> <sha>` | No |
| History and authorship | `git log`, `git blame <sha> -- <path>` | No |

Because the review never reads the worktree, the user's pending changes are **structurally incapable** of interfering. That is a stronger guarantee than a worktree provides, and it costs nothing to maintain.

A worktree, by contrast, is mutable. Something writes into it and the review's view of the code is now subtly wrong with no way to detect it. A commit SHA cannot drift.

Concurrency makes the comparison worse still. Several reviews of the same repository at different commits are, against the object database, several read operations on a shared read gate. With worktrees they are several directories, several full checkouts, and several copies of the repository on disk.

And the lifecycle rules in the original sketch were quietly destructive. "Delete the worktree when the pull request disappears from the list" discards whatever the user had left in it, in an application that went to considerable lengths in Phase 1 — pre-image objects, an undo affordance — never to lose work.

## Accepted cost

Opening a pull request still fetches `refs/pull/N/head` into the user's repository, which writes objects and a ref into their `.git`. It never touches their index or worktree, and it takes the network gate rather than the worktree gate, so it cannot block them. Their loose object count grows until `git gc` runs.

## The Phase 3 seam

Phase 3's AI will read code *around* a change, not only the change, and may do so for several reviews of one repository concurrently while the user has local edits. The abstraction is therefore a revision-pinned view of the repository, not a file reader:

```csharp
public interface IReviewTree            // pinned to exactly one commit
{
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(FilePath path, CancellationToken ct);
    ValueTask<IReadOnlyList<FilePath>> ListAsync(FilePath prefix, CancellationToken ct);
    ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string pattern, CancellationToken ct);

    string? MaterialisedPath { get; }   // always null in Phase 2
}
```

The shape is the point. There is no way to *express* "read the working copy", so an agent seeing the user's uncommitted edits is not a bug to be avoided by discipline — it is a state the API cannot represent. This is the same technique Phase 1 used when it made the diff target derived rather than toggled.

Phase 2 implements `Read`, `List` and `Search`, all of which Phase 2 uses anyway.

### Materialisation, deferred

One class of tool genuinely needs a directory: anything that loads a solution (Roslyn's `MSBuildWorkspace`), a language server, or a test run. If Phase 3 needs that, `MaterialisedPath` becomes non-null and the mechanism is a **SHA-keyed immutable export**:

```
git archive --format=tar <sha> | tar -x -C <cache>/<sha>
```

An export touches no index, registers nothing in the repository, is immutable and therefore safely shared between concurrent reviews, is keyed by commit and therefore cacheable, and is removed with a directory delete.

`git worktree add` is the alternative, and wins only if the agent must run `git` from inside that directory. It is recorded here as rejected for Phase 3's likely needs, with the concurrency and drift argument above as the reason.

Phase 2 writes no materialisation code. The strategy is documented so Phase 2 is provably sufficient for Phase 3 without guessing at the Copilot SDK's requirements.

---

# The Pull Request Diff

## Three-dot, always

A pull request's changed files are the **three-dot** diff: `merge-base(base, head)` to `head`.

Two-dot `base..head` additionally shows everything that landed on the base branch since the branch diverged, inverted. That is both the wrong file list and the wrong line numbers.

This is a correctness constraint, not a preference. GitHub anchors review comments to `(path, line, side)`, where `LEFT` is the merge-base version of the file. Compute a different diff and every `LEFT` line number disagrees with every comment GitHub sends.

Use the `A...B` form. The `--merge-base` flag landed in git 2.30, which is exactly the Phase 1 minimum, and there is no reason to sit on the version floor when triple-dot has existed forever.

## Merge base authority

The merge base moves whenever the base branch advances. This is not an invalidation problem: Phase 1's cache is content addressed, so a new merge base is simply a new key.

Resolution order:

1. Fetch the base ref and `refs/pull/N/head`.
2. Compute `git merge-base` locally.
3. Compare against the merge base GitHub reports.
4. On mismatch, refetch and recompute.
5. If it still differs, **GitHub wins**, because GitHub is what the existing comments were anchored against.

Local computation first means the diff can render without waiting on the API. The cross-check costs one field on a response already being requested.

## Fetching refs

```
git fetch <base-repo-remote> refs/pull/N/head:refs/codereviewr/pr/<n>
```

`refs/pull/N/head` lives on the **base** repository, which is what makes fork pull requests work without adding the fork as a remote. If no local remote resolves to the base repository, fetch by URL rather than adding a remote, so the user's remote list is never polluted.

Refs under `refs/codereviewr/` are owned by the application and pruned when a pull request closes or leaves the inbox. Pruning a ref discards nothing the user created.

## Diff scope

`FileDiff.Target` is a `DiffTarget` enum whose three values name Git's three trees. A pull request diff is none of them.

`DiffTarget` is replaced on `FileDiff` by a union:

```csharp
public abstract record DiffScope
{
    public sealed record WorkingCopy(DiffTarget Target) : DiffScope;
    public sealed record Revisions(CommitId Base, CommitId Head) : DiffScope;
}
```

Staging and discard APIs accept `WorkingCopy` only. "You cannot stage from a pull request diff" therefore becomes a compile-time fact rather than a runtime guard, which is the same reasoning that led Phase 1 to derive the diff target instead of exposing a toggle.

## Progressive loading

Opening a pull request:

1. Render immediately from cache if the pull request has been opened before.
2. Otherwise render the file list as soon as `git diff --raw` returns.
3. Fetch refs in the background, with progress, if the head is not present locally.
4. Diffs, comments and check status arrive independently and repaint in place.

Nothing waits for everything.

---

# The Inbox

## Sections

| Section | Query |
| --- | --- |
| Needs My Review | `is:open is:pr review-requested:@me`, plus `team-review-requested:<org/team>` for each of the user's teams |
| Reviewed | `is:open is:pr reviewed-by:@me` |
| My Pull Requests | `is:open is:pr author:@me` |

Three corrections are encoded here, and each one is a bug in the original draft:

- **"Assigned To Me" names the wrong GitHub field.** `assignee:@me` is issue assignment and is unrelated to review requests. The section is renamed to what the query actually asks. `assignee:@me` is available as an optional extra clause for teams that work that way.
- **Team review requests do not match `review-requested:@me`.** Organisations that request reviews exclusively through teams would see a permanently empty inbox.
- **Submitting a review removes you from the requested reviewers.** So `review-requested:@me` alone cannot satisfy "approved pull requests remain until merged" — they would vanish the instant you approve. That is what the Reviewed section is for.

Merged and closed pull requests disappear because the queries are `is:open`.

## List contents

Each entry displays:

- Title and number
- Repository, and owning account where more than one is configured
- Author
- Head and base branch
- Draft indicator
- Review decision
- Check status roll-up
- Changed file count
- Unresolved thread count
- Last updated

---

# Review Experience

## Layout

Unchanged from Phase 1. Navigator, file list, diff viewer.

The reviewer should not have to learn a second interface to review someone else's code instead of their own.

## File list

Replaces staged and unstaged with a single list of changed files, plus a conversation entry:

```
Conversation
Authentication.cs
LoginController.cs
UserService.cs
```

- Alphabetical by path. Phase 3 replaces this with an AI-suggested order.
- Filter box at the top.
- Keyboard navigation to next and previous file.

## File status

The original draft listed Viewed, Commented, Unresolved and Reviewed as states, showed five glyphs, and never distinguished Viewed from Reviewed. The model collapses to **one stored bit and two derived indicators**:

| Indicator | Source |
| --- | --- |
| Viewed | A bit the reviewer sets. The only stored value. |
| Stale | Derived: the file's `ContentId` changed since it was marked viewed. |
| Comment and unresolved counts | Derived from thread data. Never set by the user. |

Staleness detection here is better than GitHub's. GitHub's `DISMISSED` state is its own approximation of "changed since viewed"; ours falls directly out of Phase 1's content addressing and is exact.

The viewed bit is **synchronised with GitHub** through `markFileAsViewed` and `unmarkFileAsViewed`, so a reviewer who starts on the web and finishes in the application sees one consistent set of ticks. Toggles ride the outbox, so it works offline. Where an Enterprise instance lacks the mutation, the bit is local-only for that account and the UI says so.

## Review progress

```
42 / 87 files viewed
6 comments
2 unresolved
```

Held until the pull request is merged or closed.

## Pull request context

All read-only, and all part of the single query that opens the pull request:

- Description, rendered as markdown
- Conversation timeline — issue-level comments and review-level summaries, which are distinct from inline threads
- Review decision, and who approved or requested changes
- Check status summary, with links out to the run
- Mergeable state
- Head movement, including force-push detection

The description and the check status are not optional. A reviewer reads the description first, and nobody approves without knowing whether CI passed. Omitting either sends the user to a browser, which is precisely what this phase exists to stop.

Head movement is nearly free — the head SHA is already tracked per synchronisation for anchor migration — and it is the explanation the reviewer needs when comments suddenly move or go outdated.

Merging is **not** included. It is a write to the repository, the plan already excludes creating and editing pull requests, and it is the one action where being wrong is expensive.

---

# Comments

## Anchoring

Phase 1 designed the annotation overlay for exactly this. Comments anchor to content, never to a position in a rendering:

```csharp
public readonly record struct DiffAnchor(DiffSide Side, ContentId Content, int Line);
public readonly record struct AnnotationRange(DiffAnchor Start, DiffAnchor End);
```

Mapping from GitHub is direct:

| GitHub | Anchor |
| --- | --- |
| `side: RIGHT`, `line` | `ContentId` of `path` at `commit_id` |
| `side: LEFT`, `line` | `ContentId` of `path` at the merge base |
| `start_line` and `line` | `AnnotationRange` |

`git rev-parse <sha>:<path>` resolves a `ContentId` for negligible cost.

Diff-relative positions are rejected for the reason Phase 1 gave: `-U<n>`, the diff algorithm, whitespace handling, and unified versus side-by-side all renumber rows, and all four are user-facing toggles. Anchoring to a rendering would detach comments during ordinary interaction.

Comments are an overlay on `FileDiff`, never embedded in it. A comment fetch completing must never invalidate a diff.

## When the head moves

GitHub's behaviour is to null out `line`, retain `original_line` and `original_commit_id`, and hide the comment as outdated. That is the annoyance a better review client has the opportunity to beat, because both blobs are local.

- Migrate the anchor by diffing the old blob against the new one. This is the mechanism Phase 1 already specified.
- A comment that migrates stays **inline**, marked as outdated so the reviewer knows the code beneath it has changed. That the code moved is information they need, not noise to hide.
- A comment that genuinely cannot be placed drops to the file's comment list, with its context **regenerated locally** from `original_commit_id` and the blob.
- Nothing is ever silently dropped.

`diff_hunk` is stored for round-tripping and is never rendered. It is GitHub-rendered patch text, and rendering it would be the single place in Phase 2 where a server-rendered diff reaches the screen.

## Supported features

"Support everything GitHub supports" is not achievable, so the plan states a list.

Supported:

- Inline comments, single-line and multi-line
- Threaded replies
- Editing and deleting your own comments
- Resolving and unresolving threads
- Markdown rendering, per the subset below
- Rendering images that appear in someone else's markdown, including assets for private repositories, which are served from authenticated, expiring URLs and therefore need a real fetch rather than a plain image source

Not supported, with reasons:

| Not supported | Reason |
| --- | --- |
| Uploading an image to a comment | **GitHub has no public API for it.** Attachment upload on github.com goes through an internal endpoint. No amount of effort changes this. |
| Applying a suggested change | Rendering a ` ```suggestion ` block as a proposed diff is in scope. Applying one commits to the author's branch, which is a repository write this phase excludes. |
| Reactions | Deferred. Not part of reviewing. |
| Mention and emoji autocomplete in the editor | Deferred. |

## Markdown

Markdig for parsing, with an Avalonia renderer written here.

The renderer is owned for the same reason `DiffViewer` is: fenced code blocks must tokenise through the existing `ISyntaxTokenService` and paint through the same `TextLayout` path, which no third-party control will do. Markdown.Avalonia is rejected — it is an Avalonia 11-era library, and adopting it would mean discovering its Avalonia 12 compatibility rather than choosing it.

Supported subset:

- Paragraphs, emphasis, strikethrough
- Inline code, and fenced code with syntax highlighting
- Lists, and read-only task lists
- Tables
- Blockquotes and GitHub alerts
- Links, autolinks, and linkification of `@mentions`, `#issue` references and commit SHAs
- Images, per above
- ` ```suggestion ` blocks, rendered as a read-only proposed diff

The editor is a plain text box with a preview toggle.

---

# Pending Reviews and the Outbox

## GitHub's pending review is the source of truth

The decisive fact: **GitHub permits exactly one pending review per user per pull request**, and pending reviews live on the server. A user who starts a review on github.com and then opens this application already has a `PENDING` review holding draft comments. A purely local draft model would still have to read and reconcile that, and would risk colliding with it on submit.

So drafts are server-side, and the application keeps a **local write-behind queue** in front of them.

## Upload and submit are different verbs

This distinction resolves a contradiction in the original draft, which said both that nothing is submitted until the reviewer acts, and that offline comments upload automatically when connectivity returns. Read together, those submit an unfinished review.

| Verb | Meaning | Trigger |
| --- | --- | --- |
| Upload | Attach a draft comment to the server-side pending review | Automatic, whenever connectivity allows |
| Submit | Approve, Comment, or Request Changes | **Explicit user action, always** |

Submission is never automatic under any circumstance, including reconnection.

## Reconciliation, not rollback

Writing a comment displays it instantly and enqueues the upload. This is exactly Phase 1's optimistic model, reused rather than reinvented:

- Displayed state is authoritative server state plus a set of pending mutations.
- On completion, success and failure identically, the pending mutation is dropped and state is re-derived.
- Failure needs no code path of its own. The pending overlay evaporates and the truth shows through.

Phase 1's rule that predicted content never enters the content-addressed cache carries over unchanged.

## Submitting when the head has moved

Comments anchored to a stale commit arrive already outdated, which is a bad outcome to reach silently.

On submit, verify the head SHA. If it moved, stop and show what changed, offering to migrate the anchors and resubmit. Never post silently against a stale head.

---

# Offline

If GitHub is unreachable, everything that reads local data keeps working:

- Read a previously opened pull request, from cache
- Read and navigate every diff, which never needed the network
- Write comments, replies and edits
- Toggle viewed
- Write notes

Those mutations queue in the outbox and are marked pending. When connectivity returns the queue drains, attaching drafts to the pending review.

Not available offline: opening a pull request never opened before, refreshing the inbox, and submitting a review.

The queue is durable user data. A comment written offline and lost to a crash is data loss, and the storage design treats it accordingly.

---

# Local Notes

One private markdown scratchpad per pull request. Never synchronised with GitHub.

```
Need to discuss with John.
Verify tests.
Check caching implementation.
```

Per-file and per-line notes are rejected for Phase 2. A per-line note needs anchoring, and anchoring needs migration when the head moves, which means building a second copy of the riskiest machinery in this plan for data GitHub will never see.

There is also real overlap with an unsent draft comment, which already covers "something I want to say but haven't". A note is specifically something the reviewer will never say.

---

# Synchronisation

Sources of refresh:

- Manual refresh
- Window activation, debounced

That is all. **No polling, and no live updates.** The Phase 1 roadmap's promise of live updates is withdrawn.

The reason is concrete rather than philosophical: GraphQL has no conditional-request mechanism. Where REST would return a `304` at no cost against the rate limit, every GraphQL refresh pays full points and a full response body. A conservative cadence is a consequence of the API choice, and the API choice is forced by thread resolution.

Webhooks are not available to a desktop application without a server, and a server is not something this product has.

The REST Notifications API — which honours `If-Modified-Since`, advertises `X-Poll-Interval`, and whose `304`s are free — is noted as the one viable route to near-live updates at negligible cost, and deferred. It only covers threads the user is subscribed to, and notification settings vary enormously between users, so it is a hint rather than a source of truth.

---

# Persistence

Phase 2 accumulates two kinds of state with opposite requirements, and conflating them is how a cache-clearing bug eats someone's private notes.

| Class | Contents | On schema change |
| --- | --- | --- |
| **Disposable cache** | Pull request metadata, comments, threads, check status, the repository index | Wipe and refetch. No migration. |
| **Durable user data** | The outbox, local notes, locally-held viewed state | Migrate. Never wipe. No other copy exists. |

They are stored separately and versioned separately.

## SQLite

`Microsoft.Data.Sqlite`, in the application data directory.

Phase 1 had no database and did not need one. Phase 2 does, because the outbox is user data whose loss is unacceptable, and a transaction is a correct enqueue where a hand-rolled append-only journal is a correct enqueue only after you have written the compaction and crash-recovery paths and tested them. Queries such as "unresolved threads across all open pull requests" come along free, and Phase 3's AI output has somewhere to go without a second migration.

Accepted cost: a native dependency in a self-contained per-RID publish.

## What is not persisted

Diffs. They are cheap to regenerate from local objects, and Phase 1's cache is deliberately in-memory and content addressed. Persisting them would add an invalidation problem that content addressing exists to avoid.

---

# Keyboard and Filtering

Keyboard:

- Next file, previous file
- Toggle viewed
- Open filter
- Next comment, previous comment
- Comment on selection
- Submit review

Filters:

- Filename
- Viewed, not viewed
- Stale
- Commented
- Unresolved

---

# Settings

- Accounts: add, remove, re-authenticate
- Enterprise host URLs
- Development Folder, scan depth, ignore list
- Repository-to-account bindings
- Default diff mode
- Everything from Phase 1, unchanged

---

# Internal Architecture

```
GitHub
  ↓
IGitHubClient           transport, auth, rate limits, capability probe
  ↓
IPullRequestService     inbox queries, PR metadata, checks
IReviewCommentService   threads, comments, resolution
  ↓
IReviewService          orchestration; owns a ReviewSession
  ↓
ReviewSession           one PR: scope, anchors, viewed state, outbox
  ↓
IReviewTree ── Diff Engine (Phase 1)
  ↓
UI
```

GitHub is a service. It is not the application model.

## New services

| Service | Responsibility |
| --- | --- |
| `IGitHubClient` | GraphQL transport, authentication, rate-limit accounting, capability probe |
| `IPullRequestService` | Inbox queries, pull request metadata, check status |
| `IReviewCommentService` | Threads, comments, resolution, viewed state |
| `IReviewService` | Opens a pull request, owns the `ReviewSession` |
| `IReviewSessionStore` | Durable review state and disposable cache, over SQLite |
| `IReviewOutbox` | Durable queue of unsent mutations, with drain and retry |
| `IRepositoryLocator` | Development Folder scan, remote normalisation, matching, clone |
| `IReviewTree` | Revision-pinned repository view |
| `ITokenStore` | Keychain-backed token storage |
| `IAccountService` | Accounts, bindings, re-authentication state |

Each independently testable, and none of them depends on Avalonia. The Phase 1 architecture test extends to cover them.

---

# Performance

## Budgets

Warm, P95. Local and network budgets are stated separately, because they fail for different reasons.

| Action | Target |
| --- | --- |
| Inbox refresh to list visible | 800 ms, network |
| Open pull request to file list visible, objects present | 400 ms |
| Open pull request, refs not present, to progress visible | 100 ms |
| Select file in a pull request, uncached diff, to first paint | 100 ms |
| Select file, cached diff, to first paint | 16 ms |
| Switch unified to side-by-side | 16 ms |
| Add a comment to visible | 16 ms |
| Toggle viewed to visible | 16 ms |

## Work assertions

Gating, hardware independent, in Phase 1's style. Every performance claim above restates as a work contract:

- Opening a pull request performs exactly one GraphQL request.
- Switching files within an open pull request performs zero network requests.
- Switching view mode performs zero network requests, zero `git` invocations, and tokenises zero lines.
- Toggling viewed performs zero synchronous network requests.
- Adding a comment performs zero synchronous network requests.
- No GraphQL request ever occurs on the UI synchronisation context.
- Rendering a pull request diff performs a number of `git` invocations independent of the number of changed files.

---

# Testing

Phase 1's philosophy carries forward: prefer real temporary repositories to mocks, assert work rather than wall time in gating tests.

## The local half

`RepositoryBuilder` grows the ability to construct a base branch, a head branch, and a real merge base. Everything about the three-dot diff, anchoring, and anchor migration is then testable against real Git with no network at all.

## The GitHub half

An in-process `HttpMessageHandler` over recorded JSON fixtures. No server, no port, no flake, and it runs at unit-test speed.

Accepted cost, stated explicitly: **fixtures cannot detect GitHub schema drift.** They are a photograph, and they will keep passing after production breaks. The first signal of a breaking change will be a user-visible failure. The mitigation is discipline — fixtures are regenerated whenever a query is touched — and a nightly contract test against live GitHub remains available if that proves insufficient.

## Unit tests

- Remote URL normalisation and repository matching
- Inbox query construction, including team expansion
- Merge base resolution, including the mismatch path
- GitHub comment to `DiffAnchor` mapping, both sides
- Anchor migration across a moved head, including the unplaceable case
- Outbox enqueue, drain, retry, and crash recovery
- Capability probe and degradation
- Markdown subset rendering

## Integration tests

Real temporary repositories, real Git, fixture-backed GitHub.

- Open a pull request, fetch refs, render the three-dot diff
- Fork pull request, where the head is not on any configured remote
- Head moves; comments migrate and are marked
- Head force-pushed; force-push detected and reported
- Add a comment offline, reconnect, verify it uploads and is **not** submitted
- Submit while the head has moved; verify submission is blocked and explained
- Resolve and unresolve a thread
- Toggle viewed on an instance without the mutation; verify local fallback
- Token expired mid-session; verify one account degrades and the others do not

## Snapshot tests

Verify, extending Phase 1's set:

- Row projection and token spans, completing the unmet Phase 1 exit criterion
- Anchor migration results
- Markdown rendering trees

Never pixels.

---

# Milestones

Risk first, as in Phase 1.

The thing here that could be wrong in a way that costs a rewrite is **comment anchoring and migration**. The GraphQL client, authentication, the inbox, and the markdown renderer are all well-understood plumbing. So anchoring gets proven while changing course is still cheap.

## M0 — Phase 1 remainder

- Wire `ISyntaxTokenService` into `DiffViewer`.
- Paint intra-line spans.
- Pin row projection and token span snapshots.

Exit criteria:

- Phase 1's M2 exit criteria met in full.
- Switching view mode still tokenises zero lines.

## M1 — Read-only walking skeleton

Paste a token, see an inbox, open a pull request, read its diff.

- `ITokenStore`, accounts, `viewer` validation.
- `IGitHubClient` and the capability probe.
- Inbox queries and the three sections.
- `IRepositoryLocator`: scan, normalise, match, clone prompt.
- Ref fetch, merge base resolution, `DiffScope.Revisions`, three-dot diff in the existing viewer.
- `IReviewTree` read, list and search.

No comments. This exercises every genuinely new component end to end.

Exit criteria:

- Inbox refresh and pull request open within budget.
- Opening a pull request performs exactly one GraphQL request.
- Switching files performs zero network requests.
- No GraphQL request on the UI synchronisation context.
- Architecture test extended and passing over the new projects.

## M2 — Comments and anchoring

The risky part, finished before the write path starts.

- Thread and comment fetch, mapped onto `DiffAnchor`.
- Migration across a moved head, with outdated marking.
- Local regeneration of context for unplaceable comments.
- Markdown rendering.
- Comment display in the diff viewer and in the file comment list.

Exit criteria:

- Anchor migration snapshots pinned.
- A fork pull request, a moved head, and a force-push all render correctly.
- No GitHub-rendered patch text reaches the screen.

At this point the application is dogfoodable as a read-only review client.

## M3 — Write path

- `IReviewOutbox` over SQLite, with drain, retry and crash recovery.
- Write, edit, delete, reply.
- Resolve and unresolve.
- Viewed state, synchronised.
- Submit: Approve, Comment, Request Changes, with head verification.
- Offline behaviour end to end.

Exit criteria:

- Adding a comment and toggling viewed are within budget and perform zero synchronous network requests.
- Offline comment survives a process kill and uploads on reconnect.
- Reconnection never submits.
- Submitting against a moved head is blocked and explained.

## M4 — Context, polish, measurement

- Description, conversation timeline, review decision, check status, mergeable state.
- Notes.
- Filtering and keyboard navigation.
- Settings.
- Budgets and work assertions wired into Windows CI.

Exit criteria:

- Every budget enforced in CI.
- Coverage floor met on the new projects.

---

# Definition of Done

A developer can:

- Add one or more GitHub or Enterprise accounts
- See every review awaiting them, across every repository, in one list
- Open a review and be reading code within budget
- Read the description and check status without opening a browser
- Navigate files by keyboard, with viewed state that matches github.com
- Read, write, reply to, resolve and unresolve comments
- Submit Approve, Comment, or Request Changes
- Keep working when GitHub is unreachable, and lose nothing
- Return the next day and resume
- Never have their own uncommitted work touched, seen, or disturbed

The review experience should already be better than GitHub's web interface before any AI exists.

"Better" is not a feeling assessed at the end. It is the budget table and the work assertions, enforced per milestone.

---

# Out of Scope

Deferred from the original Phase 2 draft:

- Worktrees. Rejected outright; see No Worktrees.
- Reviewing without a local clone.
- Live updates and polling.
- Nested repository discovery.

Not in Phase 2:

- Merging pull requests
- Creating or editing pull requests
- Requesting reviewers, assignees, labels, milestones
- Converting a draft pull request to ready
- Applying suggested changes
- Reactions
- Uploading images to comments — no public API exists
- Re-running checks
- CODEOWNERS awareness
- Reviewing arbitrary compare views or commits outside a pull request
- A notifications inbox
- Per-file and per-line private notes
- Mention and emoji autocomplete
- AI of any kind
- GitLab, Azure DevOps, Bitbucket

---

# Looking Ahead — Phase 3

Phase 3 builds directly on the `ReviewSession` and `IReviewTree` introduced here.

Every pull request already has a pinned head commit, a pinned merge base, a full local object database, comments anchored to content, review progress, and cached metadata.

The AI will not analyse an isolated patch. It will analyse a complete repository at an exact commit, with access to:

- Every file, changed or not
- Full history and authorship
- Dependencies, tests, and project structure
- Existing review comments, anchored to the same content the AI is reading

Three properties of this plan are what make that work:

- **Content addressing.** An AI annotation keyed by `ContentId` is never stale, only unrequested.
- **Revision pinning.** Several analyses of one repository at different commits are concurrent reads, and none of them can observe another's state or the user's uncommitted work.
- **The annotation overlay.** AI output layers over `FileDiff` exactly as comments do. Nothing about the diff or its cache changes to accommodate it.

> **Help developers review code better, not replace them.**

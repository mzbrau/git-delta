# CodeSift - Phase 1 Implementation Plan

> **Vision:** Build the fastest, most responsive cross-platform Git client focused on code review.
>
> Long term, CodeSift will evolve into an AI-first code review platform built on GitHub Copilot SDK, but Phase 1 is intentionally focused on being an exceptional local Git client with a world-class diff experience.

---

# Product Principles

These principles should guide every implementation decision.

## Performance First

Performance is the primary feature.

The application should always feel instant.

Rules:

- Never block the UI thread.
- Every Git operation must be asynchronous.
- Every expensive operation must be interruptible, within the cancellation classes defined under Performance Architecture. "Cancel" cannot mean the same thing for a read and for a checkout.
- Background work should be prioritised based on user interaction.
- Use progressive loading.
- Cache aggressively.
- Preload likely user actions.
- Prefer optimistic UI updates where safe.
- Avoid unnecessary allocations.
- Large repositories must remain responsive.

If a Git command takes 5 seconds, the application should still feel responsive.

---

## Review First

Although Phase 1 only supports local repositories, the UI should already be designed around reviewing code rather than repository management.

Large diff viewer.

Simple file navigation.

Minimal distractions.

Git operations should stay out of the user's way.

---

## Native Desktop Experience

Use Avalonia to create a desktop application that feels native.

Requirements:

- Keyboard shortcuts
- Native scrolling
- High DPI support
- Drag and drop
- Context menus
- Light/Dark themes
- Window state persistence
- Responsive layouts

---

# Technology Stack

## Framework

- .NET 10
- Avalonia UI 12
- CommunityToolkit.Mvvm
- `Microsoft.Extensions.DependencyInjection`
- TextMateSharp for syntax tokenisation

Avalonia 12 rather than 11.3.x: the only real reason to pin to 11 was AvaloniaEdit, whose stable line targets 11, and AvaloniaEdit is not used.

## Git

Abstract Git behind interfaces.

The only Phase 1 implementation shells out to the `git` command line using CliWrap.

The UI must never know which implementation is used.

Rationale:

- Partial staging goes through `git apply --cached`, inheriting Git's exact patch semantics instead of reimplementing them.
- Clean/smudge filters, including Git LFS, are applied by Git itself. Writing blobs straight into the index would bypass them and stage the wrong bytes.
- Push and pull use the credential helpers the user already has configured (Git Credential Manager, osxkeychain, SSH agent). No bespoke token storage.
- `core.fsmonitor` and the untracked cache keep status fast on very large repositories.
- Hooks, config precedence, `.gitattributes`, and pathspec handling behave exactly as the user expects.

LibGit2Sharp is explicitly rejected for Phase 1: it exposes no hunk-level staging API, does not invoke credential helpers, and has no fsmonitor equivalent.

## Process Invocation

All Git invocation goes through CliWrap.

Rules:

- Stream stdout and stderr; never buffer an entire output into a string.
- Pass a CancellationToken to every invocation.
- Prefer NUL-delimited machine-readable output, for example `status --porcelain=v2 -z`.
- Pass `--no-optional-locks` on read-only commands so background refreshes never fight the user's terminal for the index lock.
- Never route arguments through a shell.
- Keep a long-lived `git cat-file --batch` process for object reads.
- Treat exit codes and stderr as structured results, not as text to show the user verbatim.

### Environment

A `git` subprocess that waits on input looks exactly like a frozen application. Every invocation is therefore hardened:

| Setting | Reason |
| --- | --- |
| `GIT_TERMINAL_PROMPT=0` | Fail fast instead of blocking on a terminal prompt that a GUI process has no way to answer |
| `LC_ALL=C` | stderr is parsed as a structured result, and error text is not stable across locales |
| `-c core.quotePath=false` | Non-ASCII paths otherwise return octal-escaped |
| `GIT_OPTIONAL_LOCKS=0` | Background reads never take the index lock |
| Hard timeout on network operations | An unreachable host otherwise hangs indefinitely |

Per-invocation configuration uses `-c`. The user's config is never mutated, with the single exception of the opt-in fsmonitor prompt.

## Credentials

No bespoke credential UI in Phase 1.

Authentication is delegated entirely to the user's configured credential helper. Git Credential Manager and `osxkeychain` present their own UI, which is correct behaviour on Windows and macOS and is one of the reasons the CLI backend was chosen.

- On authentication failure, detect the specific cause and surface an actionable message naming the fix.
- For SSH, that includes telling the user to load an agent when a key has a passphrase and no agent is running.
- Storing secrets is a security surface Phase 1 deliberately does not take on.

Known gap: a user with neither a credential helper nor an SSH agent gets a clear error rather than a prompt. On the supported platforms a helper is near-universal, which is what makes this an acceptable Phase 1 gap.

## Git Prerequisite

CodeSift requires Git to be installed and on the PATH. Git is not bundled.

Minimum supported version: 2.30.

Rules:

- Document the prerequisite in the README, in release notes, and in the installer where one exists.
- Detect Git during startup: resolve the executable, read `git --version`, cache the result.
- If Git is missing, or older than the minimum supported version, show a blocking, actionable message naming the required version and how to install it. Never let this surface later as a failed operation.
- Allow the Git executable path to be overridden in settings, for non-standard installations.
- Show the resolved Git path and version in the diagnostics overlay.

## Diff

`git diff` is the single source of truth for file-level structure and hunk boundaries.

The viewer and the partial staging path must read from one model. If displayed hunks come from anywhere other than Git, the patches submitted to `git apply --cached` are synthesised from a different model than the one on screen, and they will eventually fail to apply.

Invocation:

- `--diff-algorithm=histogram` by default, exposed as a setting.
- `-M -C` for rename and copy detection.
- Configurable `-U<n>`.
- `--raw` and `--numstat` for file lists, so populating the file tree never parses full patches.
- Whitespace settings map directly onto `-w`, `--ignore-space-change`, and `--ignore-blank-lines`. Never reimplement these semantics.

The parsed result is a canonical immutable `FileDiff`.

DiffPlex is not used. It cannot own hunks (no Git data model: no rename detection, no binary detection, no mode changes, no `.gitattributes` textconv or diff drivers), and it diffs whole in-memory strings, which conflicts with both large file optimisation and allocation goals.

### Extension seam

There is no swappable diff *generator*. A pluggable generator can silently break staging, which is the one thing that must agree with Git exactly.

`FileDiff` is a fixed contract. Extensibility comes from annotation layered over it:

- Intra-line refinement (Phase 1)
- Review comments (Phase 2)
- AI annotation (Phase 3)
- Semantic classification (Phase 3)

Annotations are an overlay, never embedded in `FileDiff`. This keeps the diff cache independent of comment and AI lifetimes.

Intra-line refinement is a small owned implementation behind `IIntraLineDiffer`, operating on a single already-paired line. Owning it keeps highlight quality tunable, which matters more here than in most applications.

---

# Platforms

| Platform | Status |
| --- | --- |
| Windows x64, arm64 | Supported. Primary use case. |
| macOS arm64, x64 | Supported. Primary development platform. |
| Linux | Not supported. |

Development happens on macOS. The primary use case is Windows. That asymmetry drives the CI matrix.

## CI matrix

Windows receives no incidental local testing, so Windows CI is load bearing rather than optional.

- Windows on every pull request.
- macOS nightly. It is covered locally every day, and hosted macOS runners bill at ten times the Linux rate on a private repository.
- No Linux leg.

Windows-specific coverage that macOS development will never surface:

- CRLF handling and `core.autocrlf`
- Paths beyond 260 characters, requiring `core.longpaths`
- Case-insensitive filesystem behaviour
- Process spawn cost, materially higher than on Unix, which makes the invocation batching decisions matter more

## Builds

Self-contained per RID, so there is no .NET runtime prerequisite.

- `win-x64`, `win-arm64`, `osx-arm64`, `osx-x64`
- Trimming deferred. The source-generated MVVM choice keeps it viable later.
- CI publishes archives as artifacts.

No installer and no official release at this stage. Code signing and notarisation come with that later work: unsigned macOS builds are blocked by Gatekeeper and unsigned Windows builds trigger SmartScreen, so distribution carries an Apple Developer Program membership and a Windows code-signing certificate when it happens.

## Versioning

MinVer, driven by git tags.

- Tag prefix `v`.
- CI must fetch full history. Without `fetch-depth: 0`, MinVer cannot compute height and every build reports a placeholder version.
- 7.0.0 is current stable. 8.0.0-rc.1 exists.

---

# Solution Structure

```
CodeSift.sln

src/
    CodeSift.Core          domain model, settings, abstractions
    CodeSift.Git           CliWrap invocation, porcelain parsing, the gate, cancellation classes
    CodeSift.Diff          patch parsing, FileDiff, row projection, intra-line differ, token mapping
    CodeSift.App           Avalonia views, viewmodels, the diff control, DI wiring, entry point

tests/
    CodeSift.TestSupport   builders; not a test project
    CodeSift.Core.Tests
    CodeSift.Git.Tests
    CodeSift.Diff.Tests
    CodeSift.IntegrationTests
    CodeSift.Benchmarks    BenchmarkDotNet, nightly
```

Core must contain no UI dependencies. This is enforced by an architecture test asserting that `Core`, `Git`, and `Diff` reference no Avalonia assembly, rather than by convention.

Notes on the shape:

- `App` and `UI` are one project. Splitting them buys a project boundary with no architectural rule attached, and cross-assembly styles and resources need `avares://` URIs pointing at the right assembly.
- There is no `Infrastructure` project. Process invocation belongs to `Git`, and what remains is settings and window-state persistence, which lives in `Core` until it earns a project.
- `Benchmarks` rather than `PerformanceTests`. Gating performance tests are work assertions on invocation counts and allocations, and they live in the ordinary test projects next to the code they constrain. Only wall-time benchmarks need their own host.
- `TestSupport` rather than `Testing`. It is a library consumed by tests, not a test project, and a `.Testing` suffix invites someone to wire it into the runner.
- The `Git` and `Diff` seam: `Git` owns invocation and porcelain parsing, `Diff` owns patch parsing, the model, row projection, and intra-line refinement. Row projection is pure and snapshot-testable, so the crown-jewel logic stays out of the UI and the control only paints rows it is handed.

---

# Main Window

Layout inspired by SourceTree for macOS.

```
-------------------------------------------------------
 Toolbar
-------------------------------------------------------

 Navigator

 Repository

 Working Copy

 Branches

 History          (placeholder, Phase 2)

------------------------

 File List

  Staged files
  Unstaged files
  Conflicted files

------------------------

 Diff Viewer

 Side-by-side

 Unified

-------------------------------------------------------
 Status Bar
```

History is a placeholder in Phase 1. The slot exists in the layout so Phase 2 does not require a relayout, but nothing is implemented behind it.

Panels should be resizable.

Panel sizes persisted.

Window size persisted.

Theme persisted.

---

# Repository Management

Support:

- Open repository
- Clone repository
- Recent repositories
- Refresh repository
- Repository status

No advanced repository management.

Clone is freely killable, since nothing in an existing repository changes. A cancelled clone removes the partial target directory.

---

# Working Copy

Two lists, following SourceTree:

- Staged files
- Unstaged files

A partially staged file appears in both, once per list. The duplication is intentional. It is the honest representation of a file that has both staged content and further edits.

Files can be dragged between the two lists to stage and unstage.

Conflicted files appear in a third section, read-only. See Conflicts and In-Progress State.

Display:

- Modified
- Added
- Deleted
- Renamed
- Untracked

Optional:

- Ignored

Each file shows:

- icon
- name
- path
- Git status
- partially staged indicator, when the file appears in both lists

---

# Diff Target

Git has three trees, and therefore three diffs:

| Target | Command | Meaning |
| --- | --- | --- |
| `IndexToWorktree` | `git diff` | Unstaged changes |
| `HeadToIndex` | `git diff --cached` | Staged changes |
| `HeadToWorktree` | `git diff HEAD` | Everything, combined |

## The target is derived, never toggled

Selecting a file in Unstaged files shows `IndexToWorktree`.

Selecting a file in Staged files shows `HeadToIndex`.

There is no free-floating target control in the working copy. Every staging action is therefore unambiguous, because the diff on screen is by construction the one the patch applies to.

## Why this is a correctness constraint, not a layout preference

`git apply --cached` applies to the index, so patch context must match index content.

For a partially staged file, a `HeadToWorktree` patch carries context from HEAD, which no longer matches the index. It fails to apply except by luck in untouched regions.

## Inline hunk actions

Following SourceTree, staging actions live in the diff itself, per hunk and per selected line range:

- In `IndexToWorktree`: stage hunk, stage selected lines.
- In `HeadToIndex`: unstage hunk, unstage selected lines.

## Combined review mode

`HeadToWorktree` exists as an explicit, read-only review mode, serving the Review First principle.

Partial staging actions are disabled there, and the UI states why.

This is the resolution of a real tension: Review First pulls toward one combined diff, partial staging correctness pulls toward split lists. Split is the working copy default; combined is an explicit review mode. One view is not asked to do both jobs.

## Target and caching

Because the cache is content addressed, when nothing is staged for a file the index content equals the HEAD content, so `IndexToWorktree` and `HeadToWorktree` resolve to the same cache entry.

The three targets collapse to one for free in the common case, and diverge only for genuinely partially staged files.

---

# Git Operations

Support:

## Stage

- Stage file
- Unstage file

## Partial staging

Support staging individual hunks as well as specific lines.

## Discard

Supported at file, hunk, and line granularity.

- Discard file: `git restore <path>`.
- Discard hunk or line selection: `git apply --reverse` of the selected subset against the worktree, reusing the staging patch machinery against the `IndexToWorktree` target.

Discard is categorically different from every other operation here. Staged content can be unstaged, commits can be reset, a bad checkout can be checked out again. Discarded worktree edits are gone, because Git never had them.

### Recoverable by construction

No confirmation modal. Discard immediately, then show a notification offering Undo.

Before the destructive step, write the pre-image into the object database with `git hash-object -w`. Keep path and object id in a short recently-discarded list with Restore. Untracked files work identically: hash the content before deleting.

This is the only option consistent with the Error Handling principles in this plan. Those principles say to use notifications rather than modal dialogs and to support retry, so the way to honour them for discard is to make discard recoverable, at which point the modal is unnecessary.

The result is safer *and* faster than a confirmation dialog, which is the product pitch. Modals train users to dismiss them, so they buy less safety than they appear to.

Accepted cost: pre-image objects are unreferenced and inflate the loose object count until `git gc` prunes them. They survive far longer than any plausible undo window.

## Commit

Support:

- Commit message
- Amend commit
- Validation
- Commit

## Push

- Push current branch
- Progress
- Cancellation

## Pull

`--ff-only` is the default mode.

Merge and rebase are explicit opt-in choices, never defaults.

## Branches

Support:

- Checkout
- Create
- Delete
- Rename
- Fetch

Everything else can remain in the terminal.

## Stash

A two-operation escape hatch, not a stash feature.

`git checkout` refuses to switch branches when local changes would be overwritten, and without any stash the user has no way forward inside the application.

- `stash push` offered when a checkout is blocked.
- `stash pop` offered afterwards.
- No stash browser, no stash list, no partial stashing.

---

# Conflicts and In-Progress State

Conflict *resolution* is out of scope. Conflict *display* is not, and cannot be, because the user can enter a conflicted state without this application's involvement: they rebase in a terminal, hit a conflict, and switch to this window.

A client that renders a conflicted repository as though nothing were wrong will eventually get someone to commit conflict markers.

## Detection

Detect in-progress state from the filesystem, not merely from conflicted files. A rebase can be paused with a clean index.

- `MERGE_HEAD`
- `rebase-merge` and `rebase-apply` directories
- `CHERRY_PICK_HEAD`
- `REVERT_HEAD`

Unmerged files come from porcelain v2 `u` records, which carry all three stages.

## Display and exits

- A persistent banner naming the current state and the way out.
- Abort is always available. Continue is offered when the index is clean.
- Conflicted files appear in their own section, read-only, with staging actions disabled.
- Resolution is delegated: "Open in mergetool" runs `git mergetool`, and "Open terminal here" is offered alongside it.
- Marking a file resolved once fixed externally is just `git add`, so it is supported.

No three-pane merge editor is built. This is the minimum required to be honest about state.

---

# Diff Model

Immutable, content addressed, and shared by every view mode.

```csharp
public sealed record FileDiff(
    DiffTarget Target,
    FilePath OldPath, FilePath NewPath,
    ChangeKind Change,                       // Added, Modified, Deleted, Renamed, Copied, TypeChanged
    ContentId OldContent, ContentId NewContent,
    bool IsBinary,
    IReadOnlyList<DiffHunk> Hunks,
    string RawPatch);

public sealed record DiffHunk(
    int OldStart, int OldCount,
    int NewStart, int NewCount,
    string Header,                           // preserved verbatim
    IReadOnlyList<DiffLine> Lines);

public sealed record DiffLine(
    DiffLineKind Kind,                       // Context, Added, Removed, NoNewlineAtEof
    int? OldLine, int? NewLine,
    ReadOnlyMemory<char> Text,               // slice into RawPatch
    IReadOnlyList<CharSpan>? IntraLine);
```

`RawPatch` is retained verbatim and serves three purposes: it is what `git apply --cached` consumes, it is the backing buffer every `DiffLine` slices into so a large diff costs one allocation rather than one per line, and it is what gets cached.

## Content Identity

`ContentId` is the blob SHA for committed and index content, and a content hash for worktree content.

`git diff --raw` already reports both blob SHAs on the `:100644 100644 <old> <new> M` line, so the committed and index sides are free.

`ContentId` is the basis of every cache key:

| Cache | Key |
| --- | --- |
| Parsed diff | Old `ContentId`, new `ContentId`, diff options |
| Syntax tokens | `ContentId`, grammar |
| Annotations | `DiffAnchor`, which contains a `ContentId` |

Content addressed entries never need invalidating. They simply stop being requested.

## Anchoring

Annotations anchor to content, never to a position in a rendering.

```csharp
public readonly record struct DiffAnchor(DiffSide Side, ContentId Content, int Line);
public readonly record struct AnnotationRange(DiffAnchor Start, DiffAnchor End);
```

Diff relative positions are rejected. Changing `-U<n>`, changing diff algorithm, toggling ignore whitespace, and switching between unified and side-by-side all renumber rows, and all four are user facing toggles in Phase 1. Anchoring to a rendering would detach annotations during ordinary interaction.

When content changes, annotations are migrated or marked outdated by diffing old content against new content. This is the mechanism reviewers already expect from GitHub.

## Annotation Overlay

Annotations are layered over `FileDiff`, never embedded in it, so an AI pass or comment fetch completing never invalidates a diff.

```csharp
public interface IDiffAnnotationSource
{
    ValueTask<IReadOnlyList<DiffAnnotation>> GetAsync(FileDiffKey key, CancellationToken ct);
}
```

Phase 1 ships one source: intra-line refinement. Phase 2 adds review comments. Phase 3 adds AI annotation and semantic classification. None of them change `FileDiff`.

---

# Diff Viewer

The diff viewer is the most important feature in Phase 1.

Both view modes are required, and both are pure projections of the same canonical `FileDiff`.

## Side-by-side

Traditional comparison.

Within a change block, deletions pair with additions positionally, and the shorter side is padded.

## Unified

GitHub style.

Rows flattened in patch order.

## Instant switching

Switching modes must not re-invoke Git, re-parse a patch, or re-tokenise for syntax highlighting.

Layout is the only thing recomputed. Syntax tokens are keyed by content identity, not by layout, so they survive a mode switch.

---

## Rendering

A purpose-built virtualized diff control, not AvaloniaEdit.

- Fixed row height, with virtualization keyed directly to the `DiffRow` projection, so painting is O(viewport) regardless of diff size.
- Avalonia `TextLayout` for shaping.
- TextMateSharp for tokenisation. It is a standalone tokeniser and theme engine, independent of AvaloniaEdit.
- Own gutters, per-row backgrounds, and intra-line span painting.
- Annotations hit test against rows, which are owned objects rather than editor document lines.

AvaloniaEdit is rejected:

- Side-by-side alignment needs blank padding rows on the shorter side of a change block. In a `TextDocument` that means inserting placeholder lines, which shifts line numbers and breaks `(ContentId, line)` anchoring. The row projection would have to be built anyway, while still paying for an editable document.
- Its Avalonia 12 build is `12.0.0-rc1`. The stable line targets Avalonia 11.
- Syntax highlighting does not require it.

Accepted cost: text selection is hand built. Phase 1 ships row-range selection with copy as patch, copy left, and copy right, which suits review better than character selection. Character-level selection within a row comes later.

## Syntax Highlighting

Tokenise whole file versions, never visible hunks.

TextMate grammars are stateful line to line, so a hunk beginning inside a block comment or multi-line string highlights incorrectly if tokenised alone.

- Fetch both full blobs via `cat-file --batch`.
- Tokenise each version once and cache tokens by `ContentId`.
- Map tokens onto diff lines.
- Above a configured size or line-length threshold, skip highlighting and render plain text. Whole-file tokenisation is otherwise unbounded work. This threshold is what "large file optimisation" concretely means.

## Progressive Behaviour

Fixed-height virtualization already makes painting cheap, so progressive *painting* is not the mechanism.

What is progressive:

- Patch parsing. Stream the patch and publish hunks as they arrive.
- Tokenisation. Allowed to arrive after first paint and repaint in place.

## Features

- Syntax highlighting
- Word level diff
- Line numbers
- Collapsible unchanged sections
- Whitespace indicators
- Large file optimisation
- Horizontal scrolling
- Keyboard navigation

---

# Performance Architecture

## Background Loading

Selecting a file should never block.

Workflow:

1. Show cached diff if available.
2. Show placeholder if not.
3. Generate diff in background.
4. Cancel previous request immediately if selection changes. (if expensive)

---

## Predictive Loading

When viewing a file:

Preload:

- previous file
- next file

Later:

Frequently opened files.

---

## Caching

Cache:

- Git status
- Generated diffs
- Syntax highlighting
- Repository metadata

Diffs and syntax tokens are content addressed by `ContentId`, so they are never invalidated. They simply stop being requested.

Status and repository metadata are the only entries with a genuine invalidation problem, and change detection drives it.

---

## Change Detection

Watch `.git` only. Do not watch the worktree recursively.

Sources of refresh:

- `.git` watcher. Bounded to a few dozen directories, and catches index writes, HEAD moves, and ref updates, which covers every change made from a terminal or another client.
- Window activation. The user is returning from their editor.
- Completion of the application's own writes.
- Manual refresh.
- Bounded watchers on the file or files currently displayed, so an external edit updates the open diff live. One or two paths, trivial cost.

Bursts are debounced and coalesced, with a max-batch fallback straight to a full status.

### Why not a recursive worktree watcher

It degrades badly on every platform, and always degrades into this design anyway:

- Linux inotify takes one watch per directory, recursively, and the limit is shared with every other application the user runs. A dependency tree can exhaust it, and the failure mode is dropped events rather than graceful degradation.
- Windows `ReadDirectoryChangesW` overflows its buffer during bursts such as a build or a branch switch, surfacing as `InternalBufferOverflowException`. The only correct response is a full rescan.
- macOS FSEvents coalesces and reports at directory granularity, so directories get rescanned regardless.
- Skipping ignored directories requires evaluating `.gitignore`, whose semantics are fiddly, and `git check-ignore` is per-path.

On all three platforms the correct handling of a burst is a full status. Effort therefore belongs in making status fast, not in watching precisely.

### fsmonitor

Git's built-in fsmonitor daemon transforms status time on large repositories. Built in since 2.37 on Windows and macOS; on Linux it requires an external fsmonitor hook.

- When a repository's status time exceeds a threshold, offer an explicit opt-in prompt to enable `core.fsmonitor`.
- Never enable it silently. It writes to the user's repository config.
- This is the concrete mechanism behind "large repositories must remain responsive".

---

## Optimistic UI

Scope: stage and unstage only, at file, hunk, and line granularity.

Delete, rename, discard, and commit are not optimistic. They get honest progress instead. Each is a deliberate, confirmed action where a moment of work is expected, and none can be meaningfully undone in the UI.

### Reconciliation, not rollback

Displayed state is authoritative status plus a set of pending mutations.

On completion, success and failure identically, the pending mutation is dropped and state is re-derived.

Failure therefore needs no code path of its own. The overlay evaporates and the truth shows through.

Snapshot rollback is rejected. Between the optimistic apply and the failure, the watcher may have fired, another operation may have completed, or the user may have staged something else, and restoring a snapshot silently undoes those too. Reconciliation also composes with epoch guarding, which snapshot rollback fights.

### Predicted content is never cached

Applying a known patch to known content is deterministic, so post-stage index content can be predicted exactly. That is what lets a hunk vanish from the unstaged diff instantly rather than after a round trip.

But clean filters, Git LFS in particular, can make the real blob differ from the prediction.

Rule: predicted content is display only. It never enters the content addressed cache under a predicted `ContentId`. A wrong prediction must not poison a cache whose entries are otherwise incapable of being wrong.

### Why commit is excluded

Hooks run under the CLI backend. `pre-commit` can fail, can take tens of seconds, and can modify staged files, which is exactly what a format-on-commit setup does. `prepare-commit-msg` and `commit-msg` can rewrite the message.

Post-commit state is therefore not predictable from pre-commit state.

Requirements that follow:

- Commit is a progress-bearing operation, not an instant one.
- Stream hook output into the UI.
- Support cancellation.
- Offer `--no-verify` as an explicit escape.

---

## Cancellation

Every expensive operation must be interruptible, but "cancel" cannot mean the same thing for all of them. Git has no transactional checkout, so killing writes mid-flight corrupts state.

Three classes:

| Class | Operations | Cancellation means |
| --- | --- | --- |
| Freely killable | `diff`, `status`, `log`, `cat-file`, searching, repository loading, `push` | Kill the process. Only work is lost. |
| Cancellable at dispatch only | Index writes: `apply --cached`, `add`, `reset`, `commit` | Remove from the queue if not yet started. Once spawned, let it finish. |
| Abortable, not cancellable | `checkout`, `pull`, `merge` | Request abort, then run a defined recovery. |

Notes:

- `push` is freely killable. Nothing local changes, and the server only updates refs after receiving the full pack. A kill is followed by a mandatory refresh of remote-tracking refs to confirm actual state.
- Index writes are short enough that dispatch-only cancellation costs the user nothing, and it removes the stale `index.lock` problem entirely.
- Aborting a pull runs `merge --abort` or `rebase --abort` if one is in progress.
- Aborting a checkout re-runs status and reports the intermediate state honestly. It never claims success.

Accepted cost: a checkout on the Large tier can take seconds and cannot be made free to stop. Abort exists, but the user is told the worktree may be in an intermediate state rather than silently left in one.

---

## Concurrency

Git enforces its own locking. Index-mutating commands create `.git/index.lock` and fail outright when another process holds it, so concurrency has to be designed, not merely tested for.

A tiered reader/writer gate per repository. Commands are classified by what they touch:

| Class | Commands | Gate |
| --- | --- | --- |
| Read | `diff`, `status`, `log`, `cat-file`, `show` | Shared, unbounded |
| Index write | `apply --cached`, `add`, `reset`, `commit` | Exclusive against other writes, does not block reads |
| Worktree write | `checkout`, `pull`, `merge`, discard | Exclusive against everything |
| Network | `fetch`, `push` | No gate, single flight per remote |

Worktree writes block reads because `checkout` rewrites files underneath a reader, which would otherwise observe torn state.

Network operations hold no gate. `push` touches neither index nor worktree, and a slow push must never block a diff read.

### Cross-process contention

The gate coordinates this application's processes only. A terminal, an IDE, or a background `git gc` can hold `index.lock`.

- Retry write operations with bounded backoff.
- On exhaustion, report an actionable error naming the lock file.
- Never retry indefinitely. Someone else's stuck rebase must not hang the UI.

### Epoch guarding

A status read that started before a write completes must not publish afterwards, or a freshly staged file will visibly jump back to unstaged.

- Published file list and status results carry an epoch. Stale results are discarded.
- Diffs and syntax tokens need no epoch. A content addressed result can be not-current, but never wrong, because its key names the exact bytes it describes.

---

# File List

Support:

- Folder grouping
- Expand/collapse
- Search
- Sorting

Filters:

- Modified
- Added
- Deleted
- Conflicted

Staged and unstaged are not filters. They are the two lists.

---

# Settings

- Theme
- Font size
- Default diff mode
- Ignore whitespace
- Show whitespace
- Recent repositories
- Diff algorithm, defaulting to histogram
- Context lines, the `-U<n>` value
- Syntax highlighting size cap
- Git executable path override

Settings that change how a diff is computed, meaning algorithm, context lines, and whitespace handling, are part of the diff cache key. Changing one produces new entries rather than invalidating existing ones.

---

# Error Handling

No modal dialogs for recoverable errors.

Use notifications.

Support retry.

Keep repository state consistent.

---

# Logging & Diagnostics

Structured logging through `Microsoft.Extensions.Logging` abstractions in `Core`, `Git`, and `Diff`. Serilog writes a rolling file, configured in `App` only.

Logging must be near-zero cost when disabled, and must never block the UI thread on file I/O.

## Instrumentation

Timings and counters are emitted as `System.Diagnostics.Metrics` instruments, not log lines.

Record timing for:

- Repository open
- Status refresh
- Diff generation
- Stage
- Commit
- Push
- Pull

Record counters for:

- `git` process invocations
- Bytes read from `git`
- Cache hits and misses
- Lines tokenised

One instrumentation source, three consumers:

- The developer diagnostics overlay reads it live.
- Nightly benchmarks read it.
- Gating work assertions read it through a `MeterListener`.

Log lines would force tests to parse logs and force the overlay to build parallel plumbing.

Performance is a feature and should be measurable.

---

# Testing Strategy

Testing should be a first-class concern.

Aim for:

- High coverage in Core projects (>90%)
- Comprehensive integration tests
- Work-based performance assertions gating CI, with wall-time benchmarks tracked nightly
- Minimal UI automation

---

## Test Frameworks

- NUnit
- NSubstitute
- Coverlet
- BenchmarkDotNet
- Verify.NUnit

Not used:

- Moq. Version 4.20.0 shipped SponsorLink, which scanned the user's git config email and phoned home. It was removed two patches later, but Moq remains on corporate blocklists. Switching costs almost nothing here, because the testing strategy already prefers real temporary repositories over mocks.
- AutoFixture. Redundant given the `TestSupport` builders, and it trades explicit failures for magic.

---

## Unit Tests

Focus on:

Core

Git abstraction

Diff generation

Caching

Cancellation

ViewModels

Optimistic updates

Error handling

No UI.

Very fast.

---

## Component Tests

Use real temporary repositories instead of mocks wherever possible.

Example:

```
Temp Repository

↓

Modify files

↓

Stage

↓

Commit

↓

Verify behaviour
```

These provide much more confidence than mocks alone.

---

## Integration Tests

Create repositories dynamically.

Example scenarios:

- Open repository
- Stage file
- Partial stage
- Partial stage in a repository with a clean filter configured, covering the Git LFS class of bug
- Discard, and undo from the pre-image
- Commit, including a repository with a `pre-commit` hook that modifies staged files
- Push
- Pull
- Branch checkout
- Rename
- Delete
- Large repositories
- Conflicted repository is displayed as conflicted, never as clean
- Aborted checkout and aborted pull are reported accurately
- `index.lock` held by another process
- Git missing, and Git older than the minimum supported version

---

## Performance Tests

Two tiers. CI runs on standard GitHub-hosted runners; there is no dedicated benchmark machine.

### Tier 1: work assertions, gating

These gate pull requests. They assert *work*, not time, so they are hardware independent, deterministic, and run in milliseconds.

Measured quantities:

- `git` process invocation count
- Bytes read from `git`
- Allocated bytes
- Cache hit and miss counts
- Lines tokenised
- Whether anything touched the UI synchronisation context

Every performance claim in this plan restates as a work contract:

- Selecting an already cached file performs zero `git` invocations.
- Switching view mode performs zero `git` invocations and tokenises zero lines.
- Staging a hunk performs exactly one index-mutating invocation.
- Opening a repository performs a number of invocations independent of file count.
- No `git` invocation ever occurs on the UI synchronisation context.

These catch the regressions that actually matter, such as re-invoking `git` on selection change or re-tokenising on a mode switch.

### Tier 2: wall time, tracked not gating

BenchmarkDotNet, on a nightly schedule, with trend tracking that alerts on drift against a rolling median.

Wall-time thresholds do not gate pull requests. On shared runners, variance exceeds the regressions worth catching and hosted hardware generations drift, so thresholds either sit too loose to catch anything or flake until the job gets disabled.

Benchmark:

- Repository open
- Status refresh
- Diff generation
- File switching
- Cache hits
- Cache misses

Known gap accepted by this choice: work assertions can all pass while constant factors degrade. Nightly trend alerting is the mitigation available without dedicated hardware.

---

## Performance Budgets

Warm, P95.

| Action | Target |
| --- | --- |
| Cold start to window visible | 500 ms |
| Repository open to file list visible | 400 ms medium, 1.5 s large |
| Status refresh | 150 ms medium, 800 ms large |
| Select file, diff already cached, to first paint | 16 ms |
| Select file, uncached, under 2k lines, to first paint | 100 ms |
| Switch unified to side-by-side | 16 ms |
| Stage hunk to optimistic UI update | 16 ms |
| Stage hunk to authoritative state | 300 ms |
| Any frame while scrolling a 100k-line diff | 16 ms |

### Corpora

Synthesised, not cloned, so they are deterministic and need no network.

| Tier | Files | Commits |
| --- | --- | --- |
| Small | 500 | 1,000 |
| Medium | 10,000 | 50,000 |
| Large | 100,000 | 250,000 |

Repository scale and file scale are different failure modes, and the diff viewer only cares about the second. A pathological single-file corpus is therefore required:

- A 100k-line source file
- A 20 MB single-line minified bundle
- A binary file
- A CRLF file
- A non-UTF8 file

"Large repository" means the Large tier. Every unqualified performance claim in this plan refers to the Medium tier.

---

## Cancellation Tests

Verify:

- Previous diff cancelled
- Background work cancelled
- No deadlocks
- Correct diff displayed

---

## Concurrency Tests

Exercise:

- Refresh
- Stage
- Checkout
- Diff generation

Running simultaneously.

---

## Snapshot Tests

Use Verify.NUnit to snapshot data, never pixels. Cross-platform bitmap snapshots differ by font rasterisation and will flake.

Snapshot:

- Unified row projection
- Side-by-side row projection
- Token spans from syntax highlighting
- Intra-line span computation
- Generated patches for hunk and line staging

Row projection is pure, which is exactly what makes it worth pinning.

---

## Test Utilities

Create a reusable testing library.

```
CodeSift.TestSupport

RepositoryBuilder

BranchBuilder

CommitBuilder

FileBuilder

LargeRepositoryBuilder

ConflictRepositoryBuilder
```

Example:

```csharp
var repository = RepositoryBuilder
    .With100ModifiedFiles()
    .WithRemote()
    .Build();
```

---

# Code Quality

Enable:

- Nullable Reference Types
- Roslyn Analyzers
- Coverlet
- Treat warnings as errors in CI and Release only. Blocking a local Debug build on an unused variable mid-refactor slows iteration without improving the shipped result.

## Composition

- CommunityToolkit.Mvvm for MVVM. Source generated, so property and command boilerplate costs nothing at runtime.
- `Microsoft.Extensions.DependencyInjection` alone. No Generic Host; it adds startup cost for configuration and lifetime machinery this application does not need.

ReactiveUI is rejected despite being Avalonia's historical default. It is reflection and allocation heavy, adds startup cost, and carries an Rx learning curve.

---

# Out of Scope

Not included in Phase 1:

- GitHub
- Pull Requests
- AI
- Merge conflict *resolution*. Detection and display are in scope; a three-pane merge editor is not.
- Blame
- History browser. A placeholder slot exists in the window layout; nothing is implemented behind it.
- Stash UI. The two-operation stash escape hatch is in scope; browsing, listing, and partial stashing are not.
- Cherry-pick
- Rebase UI
- Interactive rebase
- Linux support
- Installers, code signing, and notarisation
- Auto-update

---

# Milestones

Risk first, not workflow first.

The purpose-built diff control is the only decision in this plan that could be wrong in a way that costs a rewrite. Everything else is well-understood plumbing. So it gets proven while changing course is still cheap, rather than arriving last after the comfortable work is done.

Each milestone has exit criteria. Performance budgets attach to the milestone where the relevant path first exists, so performance is enforced continuously rather than audited at the end.

## M1 — Walking skeleton

Open a repository, list changed files, select one, see its unified diff.

Exercises every genuinely new component end to end: CliWrap invocation, porcelain v2 parsing, patch parsing, `FileDiff`, row projection, and the custom control.

Deliberately absent: staging, syntax highlighting, side-by-side, change detection.

Exit criteria:

- Cold start to window visible within budget.
- Repository open to file list within budget, Medium tier.
- Selecting a cached file performs zero `git` invocations.
- No `git` invocation on the UI synchronisation context.
- Architecture test passing: `Core`, `Git`, `Diff` reference no Avalonia assembly.

## M2 — The diff experience

The differentiator, finished before anything else starts.

- Side-by-side, and instant switching.
- Intra-line refinement.
- Syntax highlighting with whole-file tokenisation and the size cap.
- Collapsible unchanged sections.
- Whitespace indicators, horizontal scrolling, keyboard navigation.
- Row-range selection with copy as patch, copy left, copy right.

Exit criteria:

- Mode switch within budget, performing zero `git` invocations and tokenising zero lines.
- Uncached diff to first paint within budget.
- Every frame within budget while scrolling the pathological corpus.
- Row projection and token span snapshots pinned.

At this point the application is dogfoodable as a read-only review tool, before it can stage anything.

## M3 — Write path

- Stage and unstage at file, hunk, and line granularity.
- Discard with undo.
- The concurrency gate, epoch guarding, and the optimistic overlay.
- Commit, with streamed hook output, cancellation, and `--no-verify`.

Exit criteria:

- Staging a hunk performs exactly one index-mutating invocation.
- Optimistic update within budget; authoritative state within budget.
- Generated patch snapshots pinned for hunk and line staging.
- Partial staging verified against a repository with a clean filter configured, so the LFS class of bug is covered.
- Cancellation tests passing for all three classes.

## M4 — Repository lifecycle

- Branches: checkout, create, delete, rename, fetch.
- Pull `--ff-only`, with merge and rebase opt-in.
- Push.
- Conflict and in-progress state detection and display.
- The stash escape hatch.
- Change detection, and the fsmonitor opt-in prompt.

Exit criteria:

- Status refresh within budget on Medium and Large tiers.
- Concurrency tests passing with refresh, stage, checkout, and diff in flight together.
- A conflicted repository is never displayed as clean.
- Aborted checkout and aborted pull leave a state the application reports accurately.

## M5 — Polish and measurement

- Settings, window state, panel sizes, theme persistence.
- Diagnostics overlay.
- Corpora and work assertions wired into Windows CI.
- Nightly macOS leg and nightly benchmark trend tracking.

Exit criteria:

- Every budget enforced in CI.
- Coverage floor met on `Core`, `Git`, and `Diff`.

---

# Definition of Done

A developer should comfortably replace SourceTree for daily local development.

Reached at the end of M4; M5 makes it measurable and maintainable.

Supported workflow:

- Open repository
- Review local changes
- Stage changes
- Partial stage
- Discard changes
- Commit
- Push
- Pull
- Basic branch management
- Survive a conflicted repository honestly

"Significantly faster than existing Git clients" is not a feeling to be assessed at the end. It is the budget table, enforced per milestone.

---

# Future Roadmap

## Phase 2 — GitHub Code Reviews

Transform CodeSift into a complete GitHub review client.

### Features

- GitHub authentication
- Assigned PR inbox
- Open Pull Requests
- Automatic worktree checkout
- Local review using full repository context
- Inline review comments
- Pending review support
- Approve
- Comment
- Request Changes
- Review progress
- Comment synchronisation
- Thread synchronisation
- Live updates
- Draft review support
- Review sessions
- Resume where you left off

---

## Phase 3 — AI Assisted Reviews

Integrate GitHub Copilot SDK.

AI assists the reviewer instead of replacing them.

### Features

- PR summaries
- File summaries
- Suggested review order
- Reviewer guidance
- Files to skip
- Risk analysis
- Semantic change classification
- AI generated review checklist
- Incremental review
- Architecture impact
- Test coverage awareness
- Reviewer personas

Examples:

- Security
- Performance
- API Design
- Maintainability

---

## Phase 4 — Code Intelligence

Move beyond reviewing changes into understanding systems.

### Features

- Cross-file navigation
- Symbol search
- Find references
- Call hierarchy
- Dependency graphs
- Architecture visualisation
- Service summaries
- AI code explanations
- File ownership
- Historical churn
- Historical bug hotspots
- Related pull requests
- Intelligent repository search

---

## Phase 5 — Team Platform

Become a collaborative review platform.

### Features

- Team dashboards
- Review analytics
- Review templates
- AI personalised to reviewer behaviour
- Plugin system
- MCP integrations
- Local LLM support
- Azure DevOps support
- GitLab support
- Bitbucket support
- Enterprise deployment

---

# Long-Term Vision

CodeSift is **not another Git client**.

It is an **AI-first code review platform** where:

- Git becomes the transport layer.
- The UI is optimised for understanding code.
- AI reduces cognitive load instead of replacing developers.
- Performance is a competitive advantage.
- Local changes and Pull Requests share the same review experience.
- The application becomes the fastest, most capable place to understand and review code.

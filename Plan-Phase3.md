# GIT DELTA - Phase 3 Implementation Plan

> **Objective:** Transform GIT DELTA from a GitHub Pull Request client into an AI-assisted code review platform.
>
> The goal of Phase 3 is **not** to replace the human reviewer.
>
> Instead, AI should dramatically reduce the cognitive effort required to review large changes while keeping the human fully in control.
>
> Every feature should answer one question:
>
> **"How can we help a developer understand this change faster?"**

---

# Vision

Today's AI code review tools mostly produce review comments.

GIT DELTA should instead become an **AI Review Assistant**.

Rather than reviewing *for* the user, it should:

- explain changes
- prioritise changes
- provide context
- identify areas worth attention
- reduce noise
- help the reviewer spend time where it matters

The user still performs the review.

The AI acts like an experienced teammate sitting beside them.

---

# Design Principles

## Human First

The human reviewer is always in control.

AI never:

- automatically approves
- automatically rejects
- automatically submits comments
- modifies any file the user cares about

Everything is advisory. This is enforced in code, not merely asserted: the agent runs
against a throwaway export of the reviewed commit with a read-only tool allowlist, and
AI-suggested comments
can only reach GitHub by passing through the human's own draft composer and the existing
review outbox.

## Explicit First

AI work only happens when the user asks for it.

Opening a pull request performs **zero** AI calls. Every agent turn is a metered Copilot
premium request, so triaging five pull requests must never spend quota on the four the
user never reads.

## Context First

Unlike browser-based AI reviewers, GIT DELTA runs on the developer's machine with the
whole repository present. The agent is given a real checkout pinned to the pull request
head commit and explores it with its own tools: reading files, searching, following call
sites, inspecting history.

The advantage is not that we assemble more context. It is that the agent can go and get
exactly the context it needs, at the exact revision under review.

## Performance First

AI must never block the UI.

Everything runs asynchronously and is cancellable. Users continue reviewing while AI
works, and no AI failure can degrade or block the human review.

## Honest First

The product does not fabricate precision. Numbers that can be measured are measured and
labelled as facts. Judgements come from the model and are labelled as judgements, always
accompanied by the evidence that justifies them.

We do not claim guarantees we cannot enforce. Repository content is sent to GitHub's
servers; the plan says so plainly and gives the user real controls rather than a
comforting toggle that does nothing.

---

# The AI Provider

AI is powered by the **GitHub Copilot SDK** (`GitHub.Copilot.SDK` on NuGet).

The user provides their own GitHub authentication and their own Copilot subscription.

- No GIT DELTA backend.
- No GIT DELTA-operated cloud infrastructure.
- No additional subscription beyond Copilot.

## What the SDK actually is

This is load-bearing for the whole design, and differs from a conventional LLM client.

The SDK is **not** a chat-completions API. It spawns a bundled **Copilot CLI** process and
drives an *agent* over stdio. That agent has its own tools — read file, glob, grep, edit
file, run shell commands — and it uses them autonomously to explore the working directory
it is pointed at.

Consequences that shape this plan:

| Fact | Consequence |
| --- | --- |
| The agent's tools read a real filesystem directory (`CopilotClientOptions.Cwd`) | We must materialise the PR head revision to disk, or it reviews the wrong code |
| `SessionConfig.OnPermissionRequest` is **required**, because the agent will try to write files and run commands | We need an explicit deny-by-default permission policy |
| Sessions are stateful and turn-based (`CopilotSession`, `ResumeSessionAsync`) | The dominant cost is repeated repository exploration, so we reuse one session per review |
| There is no JSON-schema / structured-output mode | Structured results must come from typed **tool calls**, not from parsing prose |
| `Tools`, `AvailableTools` and `ExcludedTools` allow custom tools and tool restriction | We can both restrict the agent and give it curated capabilities |
| The SDK is a **technical preview** and may change in breaking ways between minor versions | The SDK must be quarantined behind one internal interface in one project |

The SDK version is pinned in `Directory.Packages.props` at implementation time, and SDK
upgrades are treated as deliberate, tested changes rather than routine bumps.

Future providers (not part of this implementation) may include OpenAI, Azure OpenAI,
Anthropic, or local models. The internal agent-session interface keeps that door open
without building for it speculatively.

---

# Increments

Phase 3 is delivered as three increments, split along the seam of what one agent turn
naturally produces. Each increment is independently shippable and independently valuable.

## 3.1 — Triage

All the plumbing, plus everything that falls out of a single whole-PR turn.

- `GitDelta.AI` project, DI wiring, architecture tests
- PR head materialisation and export cache
- Agent session lifecycle, permission policy, curated tools
- Tool-based structured output
- Result storage, content-addressed cache and staleness
- Settings: enable, auth probe, model override, budgets, review rules, denylist
- Request AI review button, progress dialog, cancel and resume
- **PR summary**
- **Risk rating with file-citing justifications**
- **Suggested review order**
- **Skip / review-carefully guidance**
- **Per-file semantic classification**

## 3.2 — Per-file depth

- Lazy per-file summaries, rendered above the diff
- Inline AI annotations in the diff comment lane, with unread / read / dismissed states
- "Insert Comment" into the human draft composer
- Per-file question box for targeted follow-ups
- Prioritised work queue driving lazy generation

## 3.3 — Conversation

- Review-wide AI chat, aware of repository, review, current file and selected lines
- Right-click inline actions on selected lines (explain, review, find bugs, suggest tests)
- Explicit incremental delta re-review when new commits arrive

---

# Architecture

## Project placement

One new project: **`GitDelta.AI`**.

```
GitDelta.App  ──►  GitDelta.AI  ──►  Core, Git, Diff, GitHub, Persistence, Review
```

Registered via `AddGitDeltaAI()` from `ServiceConfiguration.Build()`. **Only
`GitDelta.App` references it**, so nothing in the existing stack depends on AI and the
entire phase remains removable.

The Copilot SDK types are confined behind an internal agent-session interface
(`IAgentClient` / `IAgentSession`) in a single folder. A second provider later is a folder,
not an assembly. `IAIReviewService` and `NullAIReviewService` already exist in
`GitDelta.Core` and remain the seam through which `GitDelta.App` consumes AI
annotations as `IDiffAnnotation`.

## New architecture tests

Added to `tests/GitDelta.Core.Tests` alongside the existing Avalonia-isolation test:

- `GitHub.Copilot.SDK` may only be referenced from `GitDelta.AI`.
- `GitDelta.AI` must not reference Avalonia.

## Layering

```
    ReviewViewModel  (App)
      │
      ▼
IAIReviewService / AiReviewCoordinator      orchestration, budgets, run state
      │
      ├──► ReviewTreeMaterialiser           SHA-keyed export → MaterialisedPath
      ├──► PrFactsAssembler                 the little the agent can't discover itself
      ├──► AiPromptCatalog                  embedded, versioned templates
      ├──► AiResultStore                    durable.db, content-addressed
      ├──► AiWorkQueue                      single-consumer priority queue
      └──► IAgentSession ──► Copilot SDK ──► Copilot CLI ──► agent + tools
                  │
                  ▼
          typed tool-call results ──► strongly typed AI models ──► UI models
```

The agent never communicates directly with the UI. Every result crosses the boundary as a
strongly typed model.

---

# Materialising the PR Head

Today `ReviewService` fetches `refs/pull/{n}/head` and diffs merge-base to head without
ever checking anything out. The user's working tree is on an unrelated branch. Pointing the
agent at the clone as-is would make it review the wrong code and cite lines that do not
exist in the pull request — confidently, which is worse.

So the agent is given a directory containing exactly the PR head revision, using the
mechanism `Plan-Phase2.md` already specified: a **SHA-keyed immutable export**, filling in
the `IReviewTree.MaterialisedPath` hook that Phase 2 deliberately left null.

```
git archive --format=tar <head-sha> | tar -x -C <appdata>/GitDelta/trees/<sha>
```

- Run through `IGitProcessRunner` on the read gate. It writes nothing into the user's `.git`,
  registers no worktree, and never touches their index, working tree or current branch.
- **Keyed by commit SHA**, so it is immutable and inherently cacheable — revisiting a pull
  request whose head has not moved is free, and two reviews of the same commit share one
  export.
- Removed with a plain directory delete. No `git worktree prune` lifecycle to get wrong.
- Cleaned up lazily: exports unused for N days are deleted, plus an explicit "Clear AI data"
  action in settings.

`git worktree add --detach` was considered and rejected, consistent with the analysis in
`Plan-Phase2.md`. Its single advantage is that the agent could run `git` from inside the
directory — and the permission policy below bans shell execution outright, supplying curated
history and blame tools instead. So that advantage is worth nothing here, while the export's
advantages all survive.

Materialisation is reported as a stage of run progress, because on a large repository the
first extract for a given commit takes long enough to need reporting.

Two known characteristics of `git archive`, recorded so they are not discovered as bugs.
Paths marked `export-ignore` in `.gitattributes` are **omitted** from the export, so an agent
would report them as absent; we detect this by comparing the export against `IReviewTree`'s
listing and warn rather than silently review an incomplete tree. And Git LFS pointers are
exported as pointer files rather than real content, which is acceptable — the binary assets
this affects are not reviewable source — but means the agent must not be asked to reason
about LFS-tracked file contents.

**AI runs are still serialised to one per repository**, but now as a deliberate cost-control
choice rather than a side effect of sharing one checkout. Because exports are SHA-keyed and
immutable, concurrent runs would be technically safe; we choose not to allow them so a user
cannot accidentally have three agents spending Copilot quota at once.

---

# Agent Session Model

**One long-lived session per review.**

The expensive part of an agentic run is not the prompt; it is the agent re-exploring the
repository from scratch. A fresh session per task would multiply that cost by the number of
tasks.

**Turn 1 — whole-PR triage.** The agent explores once and reports, in a single turn, the PR
summary, risk rating with justifications, suggested review order, and the skip /
review-carefully classification for **every** changed file. This single turn produces all
of 3.1 and makes everything afterwards cheaper, because the triage tells us where depth is
worth spending.

**Later turns — depth on demand.** Per-file summaries, annotations, per-file questions,
inline actions and review chat all run as further turns in the *same* session. That is a
feature, not just an optimisation: the assistant already knows this pull request.

Session configuration:

- `InfiniteSessions` enabled for automatic context compaction on long reviews.
- The Copilot `SessionId` is persisted on the run record so `ResumeSessionAsync` can
  reattach after an app restart, a cancelled run, or when chat opens later.
- `Streaming` enabled so progress and partial results surface as they arrive.

---

# Permission Policy

`OnPermissionRequest` is mandatory, and Out of Scope forbids agentic code modification.
The policy is therefore **deny by default**.

**Allowed:** read-only built-in tools only — read file, glob / list, grep.

**Denied:** all file writes and edits, all shell command execution, everything not on the
allowlist. Denials are recorded in a structured log.

**No shell at all**, including read-only git commands. Instead we supply curated custom
tools for the capabilities the agent would otherwise want a shell for. This is both safer
and more useful, because our tools answer the question directly rather than making the
agent parse CLI output.

**No interactive permission prompts.** A permission dialog appearing mid-run during a
background review is bad UX and a security footgun; the user approved the *review*, not
arbitrary capability escalation. Where the denial log shows the agent repeatedly wanting
something legitimate, the answer is a new curated tool in the next release — not a prompt.

A **path denylist** is enforced in the same handler. The permission callback receives tool
arguments, so reads of `.env`, `*.pem`, `*.key`, credential files and a user-extensible
pattern list are hard-denied even though the file is present in the export.

---

# Custom Tools

Custom tools serve two distinct purposes.

## Input tools — curated capability

Things the agent cannot discover from the filesystem, or should not use a shell to obtain.
Each is backed by an existing GIT DELTA service.

| Tool | Backed by |
| --- | --- |
| `get_pull_request_diff` | existing merge-base to head diff services |
| `get_file_history` | `GitDelta.Git` history services |
| `get_file_blame` | `GitDelta.Git` |
| `get_review_threads` | `ReviewCommentService` |

## Output tools — the structured result channel

There is no JSON-schema mode in the SDK, and an agentic CLI will happily wrap results in
commentary or narrate its reasoning first. So **we do not parse the reply text at all**.
Reporting a result *means calling a tool*:

- `submit_pr_triage` — summary, risk, justifications, review order, per-file classification
- `submit_file_summary` — purpose, interesting changes, review focus
- `add_annotation` — path, line range, severity, message

Tool invocations arrive as typed, schema-validated calls through the SDK, so results are
strongly typed by construction. They also arrive *incrementally*, which is what makes
progressive rendering and partial-result persistence possible.

Tolerant fenced-JSON extraction with one repair retry remains only as a fallback for a
misbehaving model.

---

# PR Facts Assembler

Because the agent has a real checkout and its own tools, a heavyweight context builder is
redundant — and actively harmful, since pre-stuffing context the agent then ignores burns
tokens for nothing.

So this is a **thin facts assembler**, supplying only what the agent genuinely cannot
discover from the filesystem:

- PR metadata: title, body, author, base and head branch
- Merge-base and head SHAs
- Changed file list with change kinds
- The diff itself
- Existing review threads

Related source files, project structure, call sites and history are **tool calls**, not
prompt payload. That is cheaper and more accurate than us guessing what is relevant.

---

# Prompt System

Prompt templates live as **embedded resources** in `GitDelta.AI`, mirroring the existing
pattern where `GitDelta.GitHub` embeds its `.graphql` files. A `PromptVersion` constant
participates in every cache key, so changing a template correctly invalidates prior results.

Templates: PR triage, file summary, annotation pass, explanation, comment suggestion,
chat system message.

## Review rules

Two layers only in Phase 3:

1. **Built-in defaults** — a reasonable general-purpose review rule set.
2. **A single global user override** in settings, for the user's own review rules.

No per-repository rule configuration in Phase 3.

**Repository instructions are honoured by the agent itself.** Because the agent runs inside
the repository's files on disk, Copilot already reads the repository's own instruction files
— `.github/copilot-instructions.md` and equivalents — exactly as the GitHub Copilot review
agent does. We do not reimplement, override or duplicate this; we point the agent at the
export and let it pick them up. A team's existing written conventions therefore shape the
review with no configuration at all.

This does mean instruction files are read **at the reviewed revision**, so a pull request
that changes `.github/copilot-instructions.md` is reviewed under its own proposed rules.
That is the correct behaviour, and worth knowing when it surprises someone.

---

# Results and Storage

## Where results live

AI results are stored in **`durable.db`** as a new **schema v3** migration, not in
`cache.db`.

`cache.db` exists to be wiped. If a user clears the cache to fix a diff rendering glitch,
silently destroying AI output they paid for is a bad trade. Acknowledgement state on
annotations lives alongside the results. An explicit **"Clear AI data"** settings action
covers the deliberate case.

New tables (indicative):

- `ai_runs` — run id, PR node id, head SHA, merge-base SHA, Copilot session id, state
  (running / complete / incomplete / failed), turns used, started and finished timestamps,
  ad-hoc instructions
- `ai_pr_results` — triage artefact keyed as below
- `ai_file_results` — per-file summary and classification
- `ai_annotations` — path, blob OID, line range, severity, body, and read state
  (unread / read / dismissed)

## Cache keys are content-addressed

Not event-based. Staleness is a property of the inputs, not a signal we have to remember to
send.

- **Whole-PR triage:** PR node id + head SHA + merge-base SHA + prompt version + effective
  model + review-rules hash + ad-hoc instructions hash
- **Per-file artefacts:** path + before blob OID + after blob OID + prompt version +
  effective model + review-rules hash + ad-hoc instructions hash

## Incremental review is a consequence, not a feature

When a new commit lands, the head SHA changes so triage goes stale — but every **unchanged
file keeps its summary and annotations automatically**, because their blob OIDs are
unchanged. The "analyse only the delta" behaviour falls out of the key design rather than
requiring a bespoke delta engine.

Annotation anchoring is also simpler than for GitHub review threads: AI annotations are
keyed to `(path, blob OID, line)`, so they are exact for that blob and never need
`CommentAnchorMapper` or `AnchorMigrator`.

---

# Work Queue

The single-session decision makes turns strictly serial, so this is a reordering problem,
not a concurrency problem.

A small **purpose-built single-consumer priority queue** inside `GitDelta.AI`, one
consumer per repository, feeding the one session. Not `IHostedService`, not a Generic Host,
not a general reusable job framework.

Priorities, highest first:

1. Explicit user request (a question, an inline action)
2. The file the user currently has open
3. Whole-PR triage
4. Remaining files, in AI-suggested order

Requests for already-cached artefacts are dropped. Requests for files the user has
navigated away from are demoted, not cancelled. Cancellation is honoured everywhere.

---

# User Experience

## The Request AI Review button

Lives in the **pull request file list pane header**, next to the existing "Open in GitHub"
icon (`Views/MainWindow.axaml`, file list header block).

It is a four-state control:

| State | Label | Action |
| --- | --- | --- |
| Idle | Request AI review | Opens a dialog to optionally add instructions or context, then starts the run |
| Running | Reviewing… | Opens the progress dialog: current stage, files completed, turns used, elapsed, **Cancel** |
| Incomplete | Resume AI review | Continues, skipping every artefact already cached |
| Complete | AI review ready | Opens the report, with a **Re-run** action |

Disabled with an explanatory tooltip when AI is off, unauthenticated, offline, or the
repository is opted out.

**Ad-hoc instructions participate in the cache key.** A run with different instructions is a
genuinely different artefact and both are retained, so adding "focus on thread safety" can
never be silently answered from a cached generic review. A "Re-run" with unchanged inputs
asks whether to reuse or discard cached results.

## Where results render

Existing surfaces are reused rather than a new pane invented.

- **Whole-PR results** — a collapsible "AI review" section pinned at the top of the existing
  **Conversation** tab, which is already where whole-PR context lives.
- **Per-file summaries** — a collapsible band above the diff in the diff pane.
- **Annotations** — the existing `DiffViewer` comment lane, in a distinct colour from human
  comments.
- **Classification and priority** — badges in the file list.

## Suggested review order in the file list

"AI suggested order" becomes a third layout mode alongside flat and tree (extending
`FileListLayoutHelper`).

- It **auto-selects when a run completes** for that pull request. Immediately after the user
  clicked the button, reordering is the payoff rather than a surprise.
- The choice is remembered per pull request and trivially switched back.
- Star ratings show as badges in **all** layout modes, since a global ordering is
  incompatible with tree grouping.
- Files classified Skip collapse into a `Skip (12)` group at the bottom in AI order mode.
- The filter flyout gains a **Review carefully** chip beside the existing viewed / stale /
  commented / unresolved chips.

## Risk rating

The risk rating is a model judgement, so it must arrive with **named justifications citing
specific files**, and those justifications are rendered next to the badge. An unexplained
"🟡 Medium" is unfalsifiable and therefore ignorable; "Medium — changes token validation in
`TenantProvider.cs`" is something a reviewer can act on or disagree with.

Measured facts — files changed, lines added and removed — are computed locally and displayed
**separately** from the AI's judgement, so the reader can always see which part is measured
and which is opinion.

**There is no estimated review time.** A fabricated minute count is false precision that
costs trust the first time it is obviously wrong.

## Annotations and Insert Comment

AI may attach advisory annotations to lines. They appear in the comment lane as dots,
coloured distinctly from human comments, in three states:

- **Unread** — newly generated
- **Read** — automatically marked when the user opens the annotation
- **Dismissed** — explicitly hidden, so a reviewer can clear noise without being nagged; a
  filter chip reveals dismissed annotations again

**"Insert Comment" prefills the existing draft comment composer** at that anchor. It does not
enqueue to the outbox. The human edits it, owns it, and submits it through the normal review
path. This is the Human First principle enforced in code.

No automatic "generated by AI" footer. Once the user has edited and published it, it is
their comment.

---

# Cost and Budgets

Copilot premium requests are metered per user, and an agentic run over a large pull request
can consume many turns.

We cannot read the user's actual Copilot quota, so everything we display is **our own turn
count, honestly labelled as such**.

Three controls:

1. **Visibility** — the progress dialog shows turns used and elapsed time for the current
   run; run totals are kept on the run record.
2. **A per-review turn budget** (default around 25). On reaching it the run **pauses and
   asks** whether to continue, rather than silently stopping or silently spending.
3. **Pre-flight confirmation for large pull requests** — "46 files, this may use roughly N
   requests" — shown before the run starts.

The vague "Token limits" setting from the original plan is replaced by these, since token
limits are not something this SDK exposes to us anyway.

---

# Model Selection

`Model` is **unset by default**, so we inherit the Copilot CLI's own default and never break
when a model name is retired. The SDK is a preview and model names change; pinning one is a
rot risk.

Settings offers an optional override picker populated at runtime from `ListModelsAsync()`,
with a `ReasoningEffort` dropdown shown only for models that report supporting it.

The effective model forms part of every cache key, so switching models invalidates prior
results rather than mixing outputs from different models in one report.

---

# Settings

Under a new **AI** category in the existing settings overlay.

- **Enable AI assistance** — wires up the existing `AppSettings.AiAssistanceEnabled`, which
  currently has no consumer
- **Copilot connection** — reuses the existing GitHub account token; a **Test AI connection**
  probe reports success, "no Copilot subscription", or "token lacks Copilot access" in plain
  words; a dedicated Copilot token field is revealed if the probe fails
- **Model override** and **reasoning effort** (optional)
- **Review rules** — a text area overriding the built-in default rules
- **Per-review turn budget**
- **Per-turn and per-run timeouts**
- **Path denylist** — user-extensible patterns added to the built-in secret-file patterns
- **Repositories excluded from AI** — per-repository opt-out
- **Clear AI data** — deletes stored AI results and materialised exports

`AppSettings.AiRedactSecrets` is **removed** (see Privacy).

---

# Privacy and Safety

## Redaction is not implementable here, so we do not claim it

`AppSettings.AiRedactSecrets` currently defaults to `true` and does nothing. It cannot be
made to work: the agent reads files with its own tools and sends them to GitHub's servers,
so we cannot redact content we never see. Shipping a toggle that implies otherwise is worse
than shipping nothing, because it manufactures false confidence.

It is deleted and replaced by two mechanisms that are actually enforceable.

1. **Path denylist in the permission handler.** The permission callback receives tool
   arguments, so reads of `.env`, `*.pem`, `*.key`, credential files and user-supplied
   patterns are hard-denied. Denials are surfaced, not silent.
2. **Honest first-run disclosure**, stating that repository content is sent to GitHub
   Copilot for processing, with a **per-repository opt-out** so a sensitive repository can be
   excluded outright.

The per-repository opt-out is a privacy control and is unrelated to prompt configuration;
there are deliberately no per-repository *review rules* in Phase 3.

## Wording correction

The original Out of Scope entry "Cloud-hosted AI" was wrong — Copilot *is* cloud-hosted.
What is out of scope is any **GIT DELTA-hosted backend or third-party AI service**.

---

# Failure Modes and Degradation

Because results arrive as individual tool calls rather than one final blob, **partial results
are real and worth keeping**.

- A cancelled or failed run **persists everything that landed** and is marked `incomplete`.
- The button then offers **Resume AI review**, which skips artefacts already cached. This
  makes cancelling cheap and safe, rather than throwing away money already spent.
- **Per-turn and per-run timeouts**, both configurable, so a hung turn cannot wedge a run.
- **Absolute rule: no AI failure can break or block the human review.** Every failure is
  surfaced inside the AI section and nowhere else. The pull request, diff, comments and
  submission flow keep working exactly as they do without AI.

Specific paths handled explicitly, each with plain-language messaging:

| Failure | Behaviour |
| --- | --- |
| Bundled CLI fails to start | AI disabled for the session, diagnostic captured in the log |
| No Copilot subscription / token lacks access | First-class disabled state with the fix explained |
| Offline | Button disabled with tooltip, matching existing `IsOffline` handling |
| PR head unfetchable, or export fails (disk full, permissions) | Run never starts; reason reported |
| Agent ignores output tools | Fallback JSON extraction, one repair retry, then run marked failed |
| Turn budget reached | Run pauses and asks |
| User cancels | Partial results kept, run marked incomplete |

---

# Testing Strategy

## The test seam

"Mock the Copilot SDK" is not viable: `CopilotClient` and `CopilotSession` are concrete
classes in a preview SDK, and real calls are nondeterministic, cost money and need a
subscription.

The internal `IAgentSession` interface **is** the test seam. A fake agent session replays
**recorded scripts of tool calls**, so tests are fully deterministic with no SDK and no
network.

## Unit tests

PR facts assembler, prompt catalog and template rendering, run coordinator state machine,
cache key computation, result store, permission policy and path denylist, turn budget,
work queue prioritisation, result-to-UI model mapping.

## Integration tests

Drive the coordinator with recorded tool-call scripts and assert on the observable
consequences: correct typed models, correct rows written to `durable.db`, correct
annotations projected, correct cache hits, correct staleness when a SHA / prompt version /
model / rules hash changes, correct resume behaviour after a simulated cancel.

Materialisation tests use the existing `RepositoryBuilder` fixtures to create real
repositories and assert that the export contains exactly the expected commit's content, that
a second request for the same SHA is served from cache without re-extracting, and that the
user's `.git`, index, working tree and current branch are untouched.

## Prompt snapshot tests

`Verify.NUnit` snapshots of rendered prompt templates, so template changes must be
deliberate.

## Response snapshot tests

Snapshot the typed models and UI models produced from a fixed tool-call script, so parsing
and mapping regressions are caught.

## Contract test

One thin opt-in test asserting the SDK adapter constructs, starts and completes a trivial
turn against the real client. Marked `[Category("RequiresCopilot")]` and excluded from CI.
This is the canary for preview-SDK breaking changes.

## Performance tests

Re-scoped, since context generation is no longer something we do:

- Export materialisation on a large repository, and the cached-hit path
- Cache lookup and staleness computation
- Projecting several hundred annotations into `DiffViewer` for a large pull request
- File list rebuild in AI suggested order for a large pull request

## Architecture tests

The two new rules: Copilot SDK confined to `GitDelta.AI`; `GitDelta.AI` free of
Avalonia. Existing Core / Git / Diff isolation rules continue to apply.

---

# Acceptance Criteria

Stated as observable behaviour, per increment.

## 3.1 — Triage

- Opening a pull request performs zero Copilot calls.
- With AI enabled and authenticated, clicking **Request AI review** on a real pull request
  produces, within one run: a PR summary; a risk rating whose justifications cite specific
  files; a semantic classification for every changed file; and a suggested review order.
- The file list switches to AI suggested order with star badges and a collapsed Skip group,
  and can be switched back.
- Measured file and line counts are displayed separately from AI judgements.
- The run reports progress and turn count, and **Cancel** works. A cancelled run retains
  partial results and offers **Resume**, which does not re-request cached artefacts.
- Closing and reopening the app shows the same results with **zero** Copilot calls.
- Pushing a new commit marks triage stale; unchanged files retain their results.
- The agent is demonstrably reading the PR head revision, not the user's working tree, and
  the user's `.git`, index, working tree and current branch are unmodified after a run.
- Attempted writes, shell commands and denylisted path reads are all denied and logged.
- Every failure path — no subscription, offline, CLI failure, timeout, malformed output —
  leaves the human review fully usable.
- Release build is warning-free and the new architecture tests pass.

## 3.2 — Per-file depth

- Opening a file shows its AI summary, generated lazily and prioritised ahead of queued
  background work; already-cached summaries appear instantly.
- Annotations appear in the comment lane, visually distinct from human comments, and move
  through unread → read → dismissed, with dismissed recoverable via a filter.
- **Insert Comment** prefills the human draft composer at the correct anchor and reaches
  GitHub only via the existing outbox.
- The per-file question box answers a question about that file using repository context, in
  the same session.
- Annotation state survives an app restart.

## 3.3 — Conversation

- Review-wide chat answers questions with awareness of repository, review, current file and
  selected lines, reusing the existing review session.
- Right-click actions on selected diff lines produce answers inline, without leaving the
  review.
- When new commits arrive, an explicit incremental re-review updates only the changed files
  and refreshes triage.

---

# Out of Scope

Not included in Phase 3:

- Automatic review approval
- Automatic comment publishing
- Automatic PR merging
- Automatic code changes
- Agentic code modification of the user's repository
- Any GIT DELTA-hosted backend or third-party AI service
- Local LLMs
- Cross-repository analysis
- Multiple AI providers

---

# Considered and Deferred

Present in earlier drafts, deliberately not in Phase 3.

**Architecture impact diagrams.** Attractive in a mockup, but a generated dependency chain
is hard to make trustworthy and harder to keep correct. Revisit alongside the Roslyn work
sketched for Phase 4.

**Test awareness** (changed code with no tests, outdated tests, coverage). Genuinely
valuable, but doing it properly needs coverage data we do not have. The agent can already
answer "is this tested?" via chat in 3.3.

**AI personas** (security, performance, API design, accessibility). Overlaps heavily with
the review rules override, which achieves most of the same effect with one mechanism
instead of two. Revisit only if the single rules field proves insufficient in practice.

**Git history awareness as a named feature.** Not needed as a feature: once the agent has
`get_file_history` and `get_file_blame` tools, historical observations appear naturally in
summaries and answers.

**Provider abstraction as a separate assembly.** The internal interface preserves the
option; a second assembly with one provider is speculative structure.

---

# Known Risks

**The SDK is a technical preview** and may break between minor versions. Mitigated by
confining it to one project behind one interface, pinning the version, and keeping the
opt-in contract test as a canary. This is an ongoing maintenance cost the phase owns.

**Cost is real and user-visible.** Mitigated by explicit triggering, one session per review,
aggressive content-addressed caching, turn budgets and pre-flight confirmation. Worth
watching after release — if turn counts run high in practice, the triage prompt is where to
economise.

**Agent behaviour is not guaranteed.** It may ignore output tools, cite wrong line numbers,
or wander. Mitigated by tool-based output with fallback parsing, and by validating annotation
line numbers against the actual diff before display; annotations that do not resolve are
dropped rather than shown at a plausible-looking wrong line.

**Materialisation cost on very large repositories.** A full export per reviewed commit, in
both disk and extract time — and unlike a worktree, a new head commit means a fresh extract
rather than an incremental checkout. Mitigated by SHA-keyed caching, lazy deletion of stale
exports, and reporting extraction as a progress stage. If it proves painful, the fallback is
dropping filesystem access entirely and serving the agent from custom tools backed by the
revision-pinned `IReviewTree`, which is why the tool layer sits behind an interface.

---

# Looking Ahead - Phase 4

This phase is not locked in and is a concept at this stage. We will see how Phase 3 evolves
before deciding whether Phase 4 is appropriate.

Phase 4 evolves from **AI-assisted reviews** into **deep code intelligence**.

Instead of analysing only the Pull Request, GIT DELTA understands the entire codebase.

Future capabilities include:

- Cross-file semantic search
- Call hierarchy visualisation
- Architecture graphs
- Service dependency maps
- Automatic architecture documentation
- Historical code evolution
- Bug hotspot analysis
- Ownership analysis
- AI-assisted onboarding
- "Explain this subsystem"
- "Show me how requests flow through the system"
- Roslyn-powered semantic analysis integrated with AI

At this point, GIT DELTA becomes much more than a review tool—it becomes a developer's
primary environment for understanding large codebases.

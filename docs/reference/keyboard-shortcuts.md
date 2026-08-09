---
title: Keyboard shortcuts
---

# Keyboard shortcuts

All application shortcuts below are **configurable** under **Settings → Shortcuts** (and in `settings.json` under `shortcuts.bindings`). Empty gesture = unbound.

App shortcuts use **Control** on both Windows and macOS (not Cmd). Diff copy still uses the platform copy chord (Ctrl/Cmd+C).

## Defaults

### Diff and layout

| Shortcut | Action |
| --- | --- |
| **Ctrl+\\** | Toggle unified / side-by-side diff |
| **Ctrl+Shift+L** | Toggle show full file |
| **Ctrl+Shift+W** | Toggle ignore whitespace |
| **Ctrl+B** | Toggle navigator sidebar |
| **Ctrl+Shift+B** | Toggle File Panel |
| **Ctrl+Alt+F** | Toggle filter / search mode (active file list) |
| **Ctrl+Shift+T** | Toggle flat list / tree view (active file list) |

### Remote and repository

| Shortcut | Action |
| --- | --- |
| **Ctrl+Shift+P** | Push |
| **Ctrl+Shift+U** | Pull |
| **Ctrl+Shift+F** | Fetch |
| **Ctrl+Shift+G** | View remote in browser |
| **Ctrl+Shift+R** | Show repository in Finder / Explorer |
| **Ctrl+T** | Quick-open a tracked file |

### Pull request review

Active when you are in a pull request workspace. Unmodified letter keys are ignored while typing in a text field.

| Shortcut | Action |
| --- | --- |
| **Ctrl+Enter** | Submit pending comment review |
| **Ctrl+F** or **/** | Focus the file filter |
| **J** or **↓** | Next file |
| **K** or **↑** | Previous file |
| **V** | Toggle viewed on the selected file |
| **N** | Next comment thread |
| **P** | Previous comment thread |
| **C** | Focus the comment draft |
| **Esc** | Dismiss mention popup / clear draft / close expanded thread |

Arrow keys for next/previous file are built-in aliases when the primary binding is still an unmodified key.

## Comments and AI chat (fixed)

These editor-local gestures are **not** configurable:

| Shortcut | Action |
| --- | --- |
| **Enter** (AI chat) | Send message |
| **Shift+Enter** (AI chat) | New line |
| **↑ / ↓ / Enter / Tab / Esc** in mention UI | Navigate, accept, or dismiss mentions |

## Diff viewer (fixed)

| Shortcut | Action |
| --- | --- |
| **Ctrl+C** / **Cmd+C** | Copy selection (Cmd on macOS) |
| Arrow keys / Page Up / Page Down / Home / End | Navigate and scroll |

## Multi-select in file lists

| Modifier | Action |
| --- | --- |
| **Ctrl** (Windows) / **Cmd** (macOS) | Toggle individual items |
| **Shift** | Range select |

## Settings storage

Bindings are stored in the app settings file as a map of shortcut id → gesture string (for example `"Push": "Ctrl+Shift+P"`). See [Settings](./settings.md).

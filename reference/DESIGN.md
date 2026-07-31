---
name: Forge Control
colors:
  surface: '#0b1326'
  surface-dim: '#0b1326'
  surface-bright: '#31394d'
  surface-container-lowest: '#060e20'
  surface-container-low: '#131b2e'
  surface-container: '#171f33'
  surface-container-high: '#222a3d'
  surface-container-highest: '#2d3449'
  on-surface: '#dae2fd'
  on-surface-variant: '#c2c6d6'
  inverse-surface: '#dae2fd'
  inverse-on-surface: '#283044'
  outline: '#8c909f'
  outline-variant: '#424754'
  surface-tint: '#adc6ff'
  primary: '#adc6ff'
  on-primary: '#002e6a'
  primary-container: '#4d8eff'
  on-primary-container: '#00285d'
  inverse-primary: '#005ac2'
  secondary: '#b7c8e1'
  on-secondary: '#213145'
  secondary-container: '#3a4a5f'
  on-secondary-container: '#a9bad3'
  tertiary: '#ffb786'
  on-tertiary: '#502400'
  tertiary-container: '#df7412'
  on-tertiary-container: '#461f00'
  error: '#ffb4ab'
  on-error: '#690005'
  error-container: '#93000a'
  on-error-container: '#ffdad6'
  primary-fixed: '#d8e2ff'
  primary-fixed-dim: '#adc6ff'
  on-primary-fixed: '#001a42'
  on-primary-fixed-variant: '#004395'
  secondary-fixed: '#d3e4fe'
  secondary-fixed-dim: '#b7c8e1'
  on-secondary-fixed: '#0b1c30'
  on-secondary-fixed-variant: '#38485d'
  tertiary-fixed: '#ffdcc6'
  tertiary-fixed-dim: '#ffb786'
  on-tertiary-fixed: '#311400'
  on-tertiary-fixed-variant: '#723600'
  background: '#0b1326'
  on-background: '#dae2fd'
  surface-variant: '#2d3449'
typography:
  ui-sans-bold:
    fontFamily: Inter
    fontSize: 13px
    fontWeight: '600'
    lineHeight: 16px
  ui-sans-reg:
    fontFamily: Inter
    fontSize: 13px
    fontWeight: '400'
    lineHeight: 16px
  ui-sans-sm:
    fontFamily: Inter
    fontSize: 11px
    fontWeight: '500'
    lineHeight: 14px
    letterSpacing: 0.02em
  code-base:
    fontFamily: JetBrains Mono
    fontSize: 12px
    fontWeight: '400'
    lineHeight: 20px
  code-bold:
    fontFamily: JetBrains Mono
    fontSize: 12px
    fontWeight: '700'
    lineHeight: 20px
  headline-panel:
    fontFamily: Inter
    fontSize: 11px
    fontWeight: '700'
    lineHeight: 16px
    letterSpacing: 0.05em
rounded:
  sm: 0.125rem
  DEFAULT: 0.25rem
  md: 0.375rem
  lg: 0.5rem
  xl: 0.75rem
  full: 9999px
spacing:
  sidebar_narrow: 48px
  nav_column: 260px
  gutter: 1px
  padding_condensed: 4px
  padding_standard: 8px
  inset_margin: 12px
---

## Brand & Style
The design system is engineered for high-density information environments, specifically tailored for developer workflows and version control. The brand personality is technical, precise, and unobtrusive, prioritizing content (code) over interface ornamentation.

The style is **Minimalist-Technical**. It utilizes a dark, low-contrast foundation to reduce eye strain during long sessions. Visual noise is minimized by hiding secondary actions until hover or selection, ensuring the developer's focus remains on the diffs and file structures. The aesthetic is "pro-grade," favoring utility and density over whitespace.

## Colors
The palette is built on a "Deep Slate" foundation. 
- **Primary (#3b82f6):** Reserved for active states, primary call-to-actions, and branch indicators. 
- **Backgrounds:** Use `#0f172a` for the primary workspace (diff view) and `#1e293b` for sidebars and navigation panels to create subtle depth through tonal shifts rather than shadows.
- **Borders:** A consistent `#334155` is used for all internal dividers to maintain structure without high contrast.
- **Status:** Use standard developer semantics: Green for additions, Red for deletions, and Yellow for modifications, but muted to fit the dark theme.

## Typography
The system uses a dual-font approach. **Inter** handles all interface labels, menus, and headings to ensure legibility at small sizes. **JetBrains Mono** is used exclusively for code, commit hashes, and file paths.

Typography is scaled down for density. The base UI size is 13px. All code-related text uses a 12px mono font with a slightly generous line-height (20px) to improve scanability of complex diffs. Upper-case labels are used for panel headers to create clear section breaks.

## Layout & Spacing
The layout follows a strict three-column structural model:
1.  **Function Sidebar (48px):** Fixed-width icon bar for high-level mode switching (History, Search, Settings).
2.  **Navigation Column (260px):** Fixed-width list for branches, tags, and staged/unstaged files.
3.  **Main View (Fluid):** The remainder of the viewport, dedicated to the diff viewer or commit history table.

Spacing is tight. Standard padding between elements is 8px, while nested list items and toolbar buttons use 4px (condensed) spacing. Borders of 1px width serve as the primary separators between columns instead of margins or shadows.

## Elevation & Depth
This design system avoids traditional shadows to maintain a flat, professional "tool" aesthetic. Depth is communicated through:
- **Tonal Layering:** The darkest shade (`#0f172a`) is the lowest layer (main content), while the sidebar (`#1e293b`) sits visually "above" or "beside" it.
- **Active State Strokes:** Instead of elevation, active panels or selected items are indicated by a 2px primary blue left-border or a subtle background tint (`#334155`).
- **Overlays:** Only modals and tooltips use shadows (large, 15% opacity black) to separate them from the dense grid behind them.

## Shapes
Shapes are functional and sharp. We use a **Soft (1)** roundedness level (4px) for buttons, input fields, and tags. This maintains the "grid" feel of a developer tool while preventing the UI from feeling overly aggressive or dated. 
- Large containers (panels) have 0px radius.
- Small interactive elements (buttons, checkboxes) have a 4px radius.
- Indicators (branch pills) use a 2px radius for a more technical look.

## Components
- **Buttons:** Ghost style by default. Borders only appear on hover. Primary buttons use a solid `#3b82f6` fill with white text.
- **File List Items:** High-density, 24px height. Use JetBrains Mono for file names. Hover state shows a background of `#334155`.
- **Input Fields:** Inset appearance with a 1px border. No background fill when focused, only a primary blue border glow.
- **Commit Graph:** Use thin 2px lines with high-saturation colors for individual branch paths.
- **Chips/Badges:** Small, 10px font size, 2px border-radius. Used for branch names and status tags (e.g., "M" for modified).
- **Diff Viewer:** Line numbers in a separate 40px gutter, muted text color. Additions/Deletions should use full-width background highlights at 15% opacity of their respective status colors.
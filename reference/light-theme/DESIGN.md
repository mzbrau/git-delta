---
name: GitDelta
colors:
  surface: '#131313'
  surface-dim: '#131313'
  surface-bright: '#393939'
  surface-container-lowest: '#0e0e0e'
  surface-container-low: '#1c1b1b'
  surface-container: '#201f1f'
  surface-container-high: '#2a2a2a'
  surface-container-highest: '#353534'
  on-surface: '#e5e2e1'
  on-surface-variant: '#ccc4d0'
  inverse-surface: '#e5e2e1'
  inverse-on-surface: '#313030'
  outline: '#958e9a'
  outline-variant: '#4a454f'
  surface-tint: '#d9b9ff'
  primary: '#eedbff'
  on-primary: '#3d235e'
  primary-container: '#d9b9ff'
  on-primary-container: '#614683'
  inverse-primary: '#6d5290'
  secondary: '#adcae6'
  on-secondary: '#143349'
  secondary-container: '#2d4961'
  on-secondary-container: '#9cb8d4'
  tertiary: '#ffdbc8'
  on-tertiary: '#502405'
  tertiary-container: '#ffb68b'
  on-tertiary-container: '#7a4523'
  error: '#ffb4ab'
  on-error: '#690005'
  error-container: '#93000a'
  on-error-container: '#ffdad6'
  primary-fixed: '#eedcff'
  primary-fixed-dim: '#d9b9ff'
  on-primary-fixed: '#270b48'
  on-primary-fixed-variant: '#543a76'
  secondary-fixed: '#cce5ff'
  secondary-fixed-dim: '#adcae6'
  on-secondary-fixed: '#001e31'
  on-secondary-fixed-variant: '#2d4961'
  tertiary-fixed: '#ffdbc9'
  tertiary-fixed-dim: '#ffb68b'
  on-tertiary-fixed: '#321200'
  on-tertiary-fixed-variant: '#6c3a19'
  background: '#131313'
  on-background: '#e5e2e1'
  surface-variant: '#353534'
  surface-base: '#1e1e1e'
  surface-low: '#191919'
  surface-high: '#262626'
  border-subtle: '#2a2a2a'
  success-green: '#4ade80'
  error-red: '#ffb4ab'
  text-vibrant: '#f7f7f7'
  text-muted: '#a3a3a3'
typography:
  headline-xl:
    fontFamily: Inter
    fontSize: 24px
    fontWeight: '700'
    lineHeight: 32px
    letterSpacing: -0.01em
  headline-lg:
    fontFamily: Inter
    fontSize: 18px
    fontWeight: '600'
    lineHeight: 26px
    letterSpacing: -0.01em
  headline-md:
    fontFamily: Inter
    fontSize: 14px
    fontWeight: '600'
    lineHeight: 20px
  body-lg:
    fontFamily: Inter
    fontSize: 15px
    fontWeight: '400'
    lineHeight: 24px
  body-md:
    fontFamily: Inter
    fontSize: 13px
    fontWeight: '400'
    lineHeight: 20px
  label-sm:
    fontFamily: Inter
    fontSize: 11px
    fontWeight: '500'
    lineHeight: 14px
    letterSpacing: 0.01em
  sidebar-header:
    fontFamily: Inter
    fontSize: 10px
    fontWeight: '700'
    lineHeight: 12px
    letterSpacing: 0.08em
  code-md:
    fontFamily: JetBrains Mono
    fontSize: 12px
    fontWeight: '450'
    lineHeight: 18px
rounded:
  sm: 0.125rem
  DEFAULT: 0.25rem
  md: 0.375rem
  lg: 0.5rem
  xl: 0.75rem
  full: 9999px
spacing:
  unit: 4px
  stack-tight: 4px
  stack-base: 12px
  stack-loose: 24px
  gutter: 20px
  margin-safe: 24px
  sidebar-width: 240px
  file-list-width: 360px
---

## Brand & Style
The brand identity is rooted in **technical precision** and **developer focus**. It utilizes a "Developer Tooling" aesthetic—a hybrid of **Minimalism** and **Brutalism**—where the interface prioritizes information density and structural clarity over decorative elements. 

The personality is utility-driven, efficient, and sophisticated. It should evoke a sense of focused calm, similar to a high-end code editor or terminal environment. Visual hierarchy is achieved through subtle tonal shifts rather than heavy shadows, ensuring a low-strain experience for prolonged technical reviews.

## Colors
The palette is a high-sophistication "Midnight" theme. It utilizes a deep achromatic base (`#131313`) punctuated by desaturated, "fidelity" pastels. 

- **Primary (Violet):** Used for active states, indicators, and primary call-to-actions.
- **Secondary (Blue-Grey):** Used for technical metadata and supporting UI icons.
- **Tertiary (Warm Orange):** Reserved for warnings or specific status indicators.
- **Surface Strategy:** Employs a narrow range of dark greys to define functional zones. Borders are critical in this dark-on-dark scheme, using `#2a2a2a` to provide necessary separation without high-contrast fatigue.
- **Status Colors:** Success is represented by a vibrant mint green (`#4ade80`) and errors by a soft coral (`#ffb4ab`), maintaining the pastel-over-dark aesthetic.

## Typography
The system uses **Inter** for all UI-related navigation and content to ensure maximum readability at small scales. **JetBrains Mono** is introduced specifically for "Technical Content"—file paths, commit hashes, code diffs, and terminal outputs.

The typographic hierarchy relies on weight and letter spacing rather than significant size shifts. Section headers use a strictly regulated uppercase style with increased tracking (0.08em) to create a distinct visual anchor for sidebar regions. Body text remains at a compact 13px to support the high-density information requirements of an IDE.

## Layout & Spacing
The layout follows a **Fixed Multi-Pane** model common in professional IDEs. It consists of a persistent top navigation (44px height), a primary sidebar (240px), a secondary contextual pane (360px), and a flexible central workbench.

- **Rhythm:** Uses a 4px base unit. Most internal padding is 12px (`stack-base`) or 20px (`gutter`) for larger containers.
- **Separation:** Divisions are managed strictly through 1px solid borders rather than gutters or whitespace.
- **Responsiveness:** On smaller viewports, the secondary contextual pane (File List) should collapse into a drawer, while the central workbench maintains priority.

## Elevation & Depth
Depth is created through **Tonal Layering** rather than traditional shadows.
- **Floor (Background):** `#131313` acts as the base canvas.
- **Raised Surfaces (Sidebars/Header):** Use `#191919` (Surface-Low) to create a slight lift.
- **Embedded Elements (Inputs/Cards):** Use `#0e0e0e` (Surface-Lowest) or `#1c1c1c` (Surface-Container) to create "wells" or "recessed" areas.
- **Active State Lift:** Selected items (like the active file) use `#262626` (Surface-High) and a 3px primary-colored accent border to indicate focus. 
- **Shadows:** Only used for floating overlays (tooltips, dropdowns, or draft comment cards) using a subtle `shadow-lg` (black with 10% opacity) to separate them from the grid.

## Shapes
The shape language is **Soft-Geometric**. 
- **Default:** Standard components (buttons, small containers) use a 2px or 4px radius.
- **Containers:** Larger cards and input fields use an 8px (`xl`) radius to feel modern and accessible.
- **Indicators:** Status pills and count badges use a 12px (`full`) radius for a distinct "pill" shape that contrasts against the rectangular grid.
- **Interactive:** Hover states should mirror the container's radius exactly to maintain alignment.

## Components
- **Buttons:** 
  - *Ghost:* No background, `on-surface-variant` text, `surface-high` on hover. Used for navigation.
  - *Solid:* Primary background with `on-primary` text. Reserved for final actions (e.g., "Post Comment").
- **Inputs:** 
  - Background: `surface-container` (`#1c1c1c`).
  - Border: `border-subtle` (`#2a2a2a`).
  - Focus: 1px Primary border with 20% primary glow (ring).
- **File List Items:** 
  - Persistent 3px left-border for active state.
  - Secondary metadata (status letters 'M', 'A') should use high-saturation status colors at 80% opacity.
- **Badges:** 
  - Small, high-contrast text on a 20-40% opaque background of the same hue (e.g., Violet text on 20% Violet background).
- **Scrollbars:** 
  - Custom 6px width, `#333333` thumb, no track background, 10px radius.
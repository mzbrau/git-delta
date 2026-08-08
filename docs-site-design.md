# Git Delta Website Front Page – Scrollytelling Design Prompt

## Vision

Design a premium, modern documentation landing page for **Git Delta**, an AI-powered Git client focused on helping developers understand and review code faster.

(see `docs/docusaurus` folder)

The page should feel similar in quality and polish to Apple's product pages or the WMF coffee machine product pages (https://www.wmf-coffeemachines.com/en_com/products/fully-automatic-coffee-machines/wmf-5000-s-plus/).

The website should not simply describe Git Delta.

**The Git Delta application itself should become the hero of the page.**

As the visitor scrolls, the application animates naturally through realistic review workflows, introducing one capability at a time.

The visitor should finish scrolling with a clear understanding of how Git Delta works before they've even installed it.

Create a premium, Apple-inspired **scrollytelling** landing page for the Git Delta documentation website.

This page will become the Docusaurus homepage and should act as a guided tour of the application rather than a traditional marketing page.

The overall experience should feel polished, modern and deliberate, similar to premium product launch pages from Apple or the WMF coffee machine website.

The application itself should become the animation.

The user should finish the landing page already understanding the product without having watched a video.

---

# Overall Design Goals

The experience should feel:

* Premium
* Smooth
* Minimal
* Modern
* Developer-focused
* Professional
* Cinematic
* Interactive

Avoid:

* Loud animations
* Bouncing effects
* Overshooting
* Flashy transitions
* Rotating UI
* Gimmicks

Every animation should have a purpose.

The color scheme for the website (the whole website, not just the landing page is contained in [design](/docs_DESIGN.md)).

---

# Technology

The project uses:

* Docusaurus
* React
* TypeScript

Recommended libraries:

* GSAP
* ScrollTrigger

Use GSAP timelines for the complete scrollytelling experience.

Use CSS transforms wherever possible to ensure GPU acceleration.

Respect `prefers-reduced-motion`.

---

# High Level Page Layout

```
Hero

↓

Pinned Scrollytelling Experience

↓

Additional Feature Cards

↓

Documentation Sections
```

Only the Hero and Scrollytelling sections require complex animation.

---

# Hero Section

When the page first loads:

Display:

* Git Delta logo
* Product name
* Tagline
* Short product description
* Primary CTA
* Secondary CTA
* Small list of quick links into the documentation

The background should be dark.

Only the **top portion** of the application screenshot should be visible at the bottom of the viewport.

The screenshot should encourage the user to continue scrolling.

---

# Chapter 1 — Enter the Application

As the user scrolls:

The screenshot should move upwards naturally until it becomes perfectly centred in the viewport.

Once centred:

Pin the screenshot.

The screenshot should now remain fixed while scrolling continues.

No spotlight yet.

Allow the user to appreciate the application.

---

# Chapter 2 — Diff Experience

Fade in a dark translucent mask across the entire screenshot.

Cut out a rounded spotlight over the diff viewer.

The spotlight should have:

* soft edges
* subtle feathering
* premium appearance

Shift the application slightly left.

This creates space for explanatory content on the right.

Display:

## Heading

Beautiful Diff Experience

Display a progressive bullet list.

The bullets should appear gradually as the user scrolls.

Features include:

* Unified diff
* Side-by-side diff
* Adjustable context lines
* View complete files
* Local review comments
* Stage individual hunks
* Stage selected lines
* Syntax highlighting
* File navigation
* Review progress

The bullet list should build progressively rather than appearing all at once.

---

## Unified → Side-by-side Transition

Continue using the same highlighted region.

Transition from the unified diff screenshot to the side-by-side diff screenshot.

Do **not** simply crossfade.

Instead:

Reveal the side-by-side screenshot using a smooth left-to-right wipe so that it appears the application itself is transforming between layouts.

The movement should be subtle and premium.

---

# Chapter 3 — Pull Request Reviews

Transition to the Pull Request screenshot.

The transition should feel like navigating within the application rather than swapping images.

Preferred transition:

A smooth bottom-to-top reveal.

Update the spotlight to the PR area.

Replace the heading.

## Heading

Review Pull Requests

Progressively reveal bullets describing:

* Browse pull requests
* Review changed files
* Inline review comments
* Create new comments
* Resolve discussions
* GitHub integration
* Review status
* Local-first workflow

---

# Chapter 4 — AI Change Briefing

Transition to the AI Overview screenshot.

Keep the application centred.

Move the spotlight.

Replace the heading.

## Heading

AI-Assisted Reviews

Reveal bullets:

* High-level change summary
* Understand changes instantly
* Highlight potential risks
* Review guidance
* Save review time

Continue scrolling.

Transition from the overall briefing to the individual file briefing.

Prefer a wipe or gentle dissolve.

Update bullets:

* File summaries
* Intent explanation
* Review suggestions
* Important areas
* Faster comprehension

---

# Chapter 5 — Light Theme

Transition from Dark Mode to Light Mode.

This should feel like the application itself is changing themes.

Avoid a simple fade.

Instead:

Reveal the Light Theme gradually from one side of the application.

The effect should feel natural.

No spotlight required.

Allow users to appreciate the entire interface.

---

# Chapter 6 — Additional Features

Unpin the application.

Scroll naturally into a grid of polished feature cards.

Possible cards:

* Apache 2.0 Licence
* Cross-platform
* GitHub Integration
* AI-powered Reviews
* Keyboard Shortcuts
* Local-first
* Fast Performance
* Privacy
* Open Source
* Modern UI

Cards should animate gently into view.

No large scroll effects are required here.

---

# Animation Principles

Animations should use:

* easing
* subtle scaling
* smooth opacity changes
* slow camera movement

Avoid:

* sudden jumps
* hard cuts
* rotating UI
* exaggerated motion

Users should always know where they are within the application.

Maintain orientation.

---

# Mobile Support

Create a dedicated mobile experience.

Do not simply scale the desktop version.

Requirements:

* Same story
* Simplified layout
* Smaller camera movements
* Reduced zoom
* Readable text
* Touch-friendly spacing
* Excellent performance

Animations may be simplified where appropriate while preserving the overall narrative.

---

# Performance

Optimise for:

* Desktop
* Laptop
* Mobile

Use:

* GPU transforms
* Lazy loading
* Image optimisation
* Minimal layout recalculation

---

# Accessibility

Support:

* Keyboard navigation
* Screen readers
* Reduced motion
* High contrast

All explanatory text should exist as real HTML, not baked into images.

---

# Image Assets

All screenshots should be captured at the **same fixed application size**.

Requirements:

* No operating system title bar
* Same dimensions
* Same scaling
* Same application window size
* High resolution
* Lossless source images

Store all screenshots in:

```
resources/
    landingpage/
```

## Before implementation

Before writing animation code, produce a complete list of every required screenshot.

For each screenshot include:

* Filename
* Purpose
* Which chapter uses it
* Notes describing exactly what should be visible

For example:

```
resources/landingpage/

01-diff-unified.png
02-diff-side-by-side.png
03-pr-review.png
04-ai-overview.png
05-ai-file-summary.png
06-light-theme.png
```

If additional screenshots are needed for smooth transitions, include them in the proposed asset list with a clear explanation of why they are required.

Do not assume screenshots exist.

The implementation should depend only on the screenshots listed in this document.

---

# Deliverables

The implementation should include:

* Clean React component structure
* Reusable animation helpers
* GSAP timelines
* ScrollTrigger integration
* Responsive layout
* Mobile implementation
* Image asset management
* Accessibility support
* Performance optimisations

---

# Success Criteria

A first-time visitor should:

1. Immediately understand what Git Delta is.
2. Experience a premium, memorable landing page.
3. Naturally discover the application's key capabilities through scrolling.
4. Finish the page already familiar with the interface.
5. Feel that the documentation site is unusually polished for a developer tool.

The final experience should feel like exploring a real application rather than reading a marketing page, with smooth, cinematic transitions that guide attention without ever becoming distracting.

One additional recommendation: ask the AI agent to implement the page as a **data-driven storyboard** rather than hard-coding each chapter. Define each chapter (heading, bullets, screenshot, spotlight rectangle, transition type, etc.) in a configuration object, and have a generic renderer build the GSAP timeline from that data. That will make it much easier to tweak copy, replace screenshots, or add future chapters without rewriting the animation logic.

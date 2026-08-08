export type TransitionType = 'none' | 'wipe-btt';

export type StoryBeat = {
  id: string;
  /** Path relative to site static root (no leading slash) */
  image: string;
  heading: string | null;
  bullets: string[];
  /**
   * Stable copy panel id. Beats that share a copyId reuse one panel
   * (image can change without fading the text).
   */
  copyId?: string;
  /** How this beat’s image enters relative to the previous visible image */
  transition: TransitionType;
  /** Shift the app frame left to make room for copy (desktop) */
  shiftLeft: boolean;
  /** Relative scroll length for this beat */
  scrollWeight: number;
};

export const LANDING_IMAGES = {
  overview: 'img/landing/01-app-overview.png',
  /** Kept on disk; not used in the active storyboard (continuity with hero peek). */
  diffUnified: 'img/landing/02-diff-unified.png',
  diffSideBySide: 'img/landing/03-diff-side-by-side.png',
  prReview: 'img/landing/04-pr-review.png',
  aiOverview: 'img/landing/05-ai-overview.png',
  aiFileSummary: 'img/landing/06-ai-file-summary.png',
  lightTheme: 'img/landing/07-light-theme.png',
} as const;

const DIFF_BULLETS = [
  'Unified diff',
  'Side-by-side diff',
  'Adjustable context lines',
  'View complete files',
  'Local review comments',
  'Stage hunks and lines',
  'Syntax highlighting',
  'Markdown viewer',
  'File metrics',
  'File filter',
  'Text search',
  'Flat list or tree view',
] as const;

/**
 * Ordered scrollytelling beats. The engine builds one scrubbed timeline from this list.
 */
export const storyBeats: StoryBeat[] = [
  {
    id: 'enter',
    image: LANDING_IMAGES.overview,
    heading: null,
    bullets: [],
    transition: 'none',
    shiftLeft: false,
    scrollWeight: 1.2,
  },
  {
    id: 'diff',
    image: LANDING_IMAGES.overview,
    heading: 'Beautiful Diff Experience',
    bullets: [...DIFF_BULLETS],
    copyId: 'diff',
    transition: 'none',
    shiftLeft: true,
    scrollWeight: 2.4,
  },
  {
    id: 'diff-side-by-side',
    image: LANDING_IMAGES.diffSideBySide,
    heading: 'Beautiful Diff Experience',
    bullets: [...DIFF_BULLETS],
    copyId: 'diff',
    transition: 'wipe-btt',
    shiftLeft: true,
    scrollWeight: 1.4,
  },
  {
    id: 'pr',
    image: LANDING_IMAGES.prReview,
    heading: 'Review Pull Requests',
    bullets: [
      'Browse pull requests',
      'Review changed files',
      'Inline review comments',
      'Create new comments',
      'Resolve discussions',
      'GitHub integration',
      'Review status',
      'Local-first workflow',
    ],
    copyId: 'pr',
    transition: 'wipe-btt',
    shiftLeft: true,
    scrollWeight: 2.2,
  },
  {
    id: 'ai',
    image: LANDING_IMAGES.aiOverview,
    heading: 'AI Change Briefing',
    bullets: [
      'High-level change summary',
      'Understand changes instantly',
      'Highlight potential risks',
      'Review guidance',
      'Save review time',
    ],
    copyId: 'ai',
    transition: 'wipe-btt',
    shiftLeft: true,
    scrollWeight: 1.8,
  },
  {
    id: 'ai-file',
    image: LANDING_IMAGES.aiFileSummary,
    heading: 'AI File Briefing',
    bullets: [
      'File summaries',
      'Intent explanation',
      'Review suggestions',
      'Important areas',
      'Faster comprehension',
    ],
    copyId: 'ai-file',
    transition: 'wipe-btt',
    shiftLeft: true,
    scrollWeight: 1.6,
  },
  {
    id: 'light',
    image: LANDING_IMAGES.lightTheme,
    heading: 'Light or dark. Same clarity.',
    bullets: [
      'Full light theme',
      'Comfortable for long sessions',
      'Same layout and density',
      'Clear contrast',
      'Easy on the eyes',
    ],
    copyId: 'light',
    transition: 'wipe-btt',
    shiftLeft: true,
    scrollWeight: 1.6,
  },
];

export const featureCards = [
  {
    title: 'Apache 2.0 Licence',
    body: 'Open source and free to use, modify, and distribute.',
  },
  {
    title: 'Cross-platform',
    body: 'Native desktop experience on Windows and macOS.',
  },
  {
    title: 'GitHub Integration',
    body: 'Inbox, pull requests, and review workflows connected to GitHub.',
  },
  {
    title: 'AI-powered Reviews',
    body: 'Optional change briefings that help you understand risk and intent.',
  },
  {
    title: 'Keyboard Shortcuts',
    body: 'Stay in flow with shortcuts for navigation, staging, and review.',
  },
  {
    title: 'Local-first',
    body: 'Your working copy and review state stay on your machine.',
  },
  {
    title: 'Fast Performance',
    body: 'Large diffs and repos stay responsive without blocking the UI.',
  },
  {
    title: 'Privacy',
    body: 'AI assist is opt-in. Tokens and reviews stay under your control.',
  },
  {
    title: 'Open Source',
    body: 'Inspect the stack, contribute, and build trust in the tooling.',
  },
  {
    title: 'Modern UI',
    body: 'A calm, dense developer interface with light and dark themes.',
  },
] as const;

export const docsLinks = [
  {
    title: 'Getting started',
    body: 'Install on Windows or macOS, then open your first repository.',
    to: '/docs/getting-started/requirements',
    label: 'Requirements →',
  },
  {
    title: 'Guides',
    body: 'Working copy, diffs, history, pull requests, and AI-assisted review.',
    to: '/docs/tour/main-window',
    label: 'App tour →',
  },
  {
    title: 'Reference',
    body: 'Settings, shortcuts, performance, and troubleshooting.',
    to: '/docs/reference/settings',
    label: 'Settings →',
  },
] as const;

/** Unique image paths in story order (for layer stacking). */
export function uniqueStoryImages(beats: StoryBeat[] = storyBeats): string[] {
  const seen = new Set<string>();
  const ordered: string[] = [];
  for (const beat of beats) {
    if (!seen.has(beat.image)) {
      seen.add(beat.image);
      ordered.push(beat.image);
    }
  }
  return ordered;
}

/** Unique copy panels (first beat that defines each copyId wins for content). */
export function uniqueCopyBeats(beats: StoryBeat[] = storyBeats): StoryBeat[] {
  const seen = new Set<string>();
  const panels: StoryBeat[] = [];
  for (const beat of beats) {
    if (!beat.heading) {
      continue;
    }
    const key = beat.copyId ?? beat.id;
    if (seen.has(key)) {
      continue;
    }
    seen.add(key);
    panels.push(beat);
  }
  return panels;
}

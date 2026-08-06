import type {SidebarsConfig} from '@docusaurus/plugin-content-docs';

const sidebars: SidebarsConfig = {
  docsSidebar: [
    {
      type: 'category',
      label: 'Introduction',
      collapsed: false,
      items: ['intro', 'why-git-delta'],
    },
    {
      type: 'category',
      label: 'Getting started',
      items: [
        'getting-started/requirements',
        'getting-started/install-windows',
        'getting-started/install-macos',
        'getting-started/first-launch',
      ],
    },
    {
      type: 'category',
      label: 'App tour',
      items: ['tour/main-window', 'tour/navigation'],
    },
    {
      type: 'category',
      label: 'Local Git',
      items: [
        'local/working-copy',
        'local/diff-viewer',
        'local/committing',
        'local/history',
        'local/branches-and-remotes',
        'local/stash',
        'local/rebase',
        'local/conflicts',
      ],
    },
    {
      type: 'category',
      label: 'Code review',
      items: [
        'review/github-accounts',
        'review/inbox',
        'review/pull-request-review',
        'review/ai-assist',
        'review/local-pending-review',
      ],
    },
    {
      type: 'category',
      label: 'Reference',
      items: [
        'reference/settings',
        'reference/keyboard-shortcuts',
        'reference/performance',
        'reference/troubleshooting',
      ],
    },
  ],
};

export default sidebars;

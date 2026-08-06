import {themes as prismThemes} from 'prism-react-renderer';
import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';

const config: Config = {
  title: 'GIT DELTA',
  tagline: 'A fast, cross-platform Git client focused on code review.',
  favicon: 'img/favicon.ico',

  future: {
    v4: true,
  },

  url: 'https://mzbrau.github.io',
  baseUrl: '/git-delta/',
  trailingSlash: true,

  organizationName: 'mzbrau',
  projectName: 'git-delta',

  onBrokenLinks: 'throw',

  markdown: {
    hooks: {
      onBrokenMarkdownImages: 'warn',
    },
  },

  i18n: {
    defaultLocale: 'en',
    locales: ['en'],
  },

  presets: [
    [
      'classic',
      {
        docs: {
          path: '..',
          exclude: ['docusaurus/**'],
          sidebarPath: './sidebars.ts',
          routeBasePath: 'docs',
          editUrl: 'https://github.com/mzbrau/git-delta/tree/main/docs/',
        },
        blog: false,
        theme: {
          customCss: './src/css/custom.css',
        },
      } satisfies Preset.Options,
    ],
  ],

  themeConfig: {
    image: 'img/docusaurus-social-card.jpg',
    colorMode: {
      respectPrefersColorScheme: true,
    },
    navbar: {
      title: 'GIT DELTA',
      items: [
        {
          type: 'docSidebar',
          sidebarId: 'docsSidebar',
          position: 'left',
          label: 'Docs',
        },
        {
          to: '/docs/getting-started/requirements',
          label: 'Getting started',
          position: 'left',
        },
        {
          href: 'https://github.com/mzbrau/git-delta',
          label: 'GitHub',
          position: 'right',
        },
        {
          href: 'https://github.com/mzbrau/git-delta/releases',
          label: 'Releases',
          position: 'right',
        },
      ],
    },
    footer: {
      style: 'dark',
      links: [
        {
          title: 'Docs',
          items: [
            {
              label: 'Introduction',
              to: '/docs/intro',
            },
            {
              label: 'Getting started',
              to: '/docs/getting-started/requirements',
            },
            {
              label: 'Why GIT DELTA',
              to: '/docs/why-git-delta',
            },
          ],
        },
        {
          title: 'More',
          items: [
            {
              label: 'GitHub',
              href: 'https://github.com/mzbrau/git-delta',
            },
            {
              label: 'Releases',
              href: 'https://github.com/mzbrau/git-delta/releases',
            },
          ],
        },
      ],
      copyright: `Copyright © ${new Date().getFullYear()} GIT DELTA. Built with Docusaurus.`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
    },
  } satisfies Preset.ThemeConfig,
};

export default config;

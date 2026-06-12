import {themes as prismThemes} from 'prism-react-renderer';
import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';

const simplePlantUML = require('@akebifiky/remark-simple-plantuml');

const config: Config = {
  title: 'IOL ECHAT Architecture',
  tagline: 'Enterprise E2EE Chat System: Architectural Documentation',
  favicon: 'img/favicon.ico',

  future: {
    v4: true,
  },

  // GitHub Pages project site: https://TheSmallPixel.github.io/ECHAT/
  url: 'https://TheSmallPixel.github.io',
  baseUrl: '/IOL-ECHAT/',

  organizationName: 'TheSmallPixel', // GitHub org/user
  projectName: 'IOL-ECHAT', // repo name

  onBrokenLinks: 'throw',

  i18n: {
    defaultLocale: 'it',
    locales: ['it'],
  },

  plugins: [
    'docusaurus-plugin-image-zoom',
  ],

  presets: [
    [
      'classic',
      {
        docs: {
          sidebarPath: './sidebars.ts',
          remarkPlugins: [simplePlantUML],
        },
        blog: false,
        theme: {
          customCss: './src/css/custom.css',
        },
      } satisfies Preset.Options,
    ],
  ],

  themeConfig: {
    image: 'img/screenshots/01-overview.png',
    colorMode: {
      respectPrefersColorScheme: true,
    },
    navbar: {
      title: 'IOL ECHAT',
      logo: {
        alt: 'ECHAT Logo',
        src: 'img/logo.svg',
      },
      items: [
        {
          type: 'docSidebar',
          sidebarId: 'archSidebar',
          position: 'left',
          label: 'Architecture',
        },
      ],
    },
    footer: {
      style: 'dark',
      copyright: `Copyright © ${new Date().getFullYear()} IOL-ECHAT Project. Built with Docusaurus.`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
    },
    zoom: {
      selector: '.markdown img',
      background: {
        light: 'rgba(255, 255, 255, 0.9)',
        dark: 'rgba(0, 0, 0, 0.9)',
      },
    },
  } satisfies Preset.ThemeConfig,
};

export default config;
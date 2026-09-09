// @ts-check
const {themes: prismThemes} = require('prism-react-renderer');

/** @type {import('@docusaurus/types').Config} */
const config = {
  title: 'Voltage Engine',
  tagline: 'A 2D game engine and editor for C# developers',
  favicon: 'img/favicon.svg',

  url: 'https://voltageengine.github.io',
  baseUrl: '/VoltageEngine/',
  trailingSlash: false,
  organizationName: 'VoltageEngine',
  projectName: 'VoltageEngine',

  onBrokenLinks: 'throw',
  onBrokenAnchors: 'warn',
  markdown: {
    mermaid: true,
    hooks: {
      onBrokenMarkdownLinks: 'warn',
    },
  },
  themes: ['@docusaurus/theme-mermaid'],

  i18n: {
    defaultLocale: 'en',
    locales: ['en'],
  },

  presets: [
    [
      'classic',
      /** @type {import('@docusaurus/preset-classic').Options} */
      ({
        docs: {
          path: '../docs',
          routeBasePath: 'docs',
          sidebarPath: require.resolve('./sidebars.js'),
          exclude: ['**/ROADMAP.md'],
          editUrl: 'https://github.com/VoltageEngine/VoltageEngine/edit/main/docs/',
        },
        blog: false,
        theme: {
          customCss: require.resolve('./src/css/custom.css'),
        },
      }),
    ],
  ],

  themeConfig:
    /** @type {import('@docusaurus/preset-classic').ThemeConfig} */
    ({
      colorMode: {
        defaultMode: 'dark',
        respectPrefersColorScheme: true,
      },
      navbar: {
        title: 'Voltage',
        logo: {
          alt: 'Voltage Engine',
          src: 'img/logo.svg',
        },
        items: [
          {
            type: 'docSidebar',
            sidebarId: 'docs',
            position: 'left',
            label: 'Docs',
          },
          {
            to: 'docs/gateway',
            position: 'left',
            label: 'Gateway',
          },
          {
            href: 'https://github.com/VoltageEngine/VoltageEngine',
            label: 'GitHub',
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
              {label: 'Introduction', to: 'docs/intro'},
              {label: 'Installation', to: 'docs/getting-started/installation'},
              {label: 'Scripting', to: 'docs/scripting/components'},
            ],
          },
          {
            title: 'Automation',
            items: [
              {label: 'Editor Gateway', to: 'docs/gateway'},
              {label: 'Plugins', to: 'docs/plugins'},
            ],
          },
          {
            title: 'Project',
            items: [
              {label: 'GitHub', href: 'https://github.com/VoltageEngine/VoltageEngine'},
              {label: 'Issues', href: 'https://github.com/VoltageEngine/VoltageEngine/issues'},
            ],
          },
        ],
        copyright: `Copyright © ${new Date().getFullYear()} Voltage Engine contributors. MIT licensed.`,
      },
      prism: {
        theme: prismThemes.github,
        darkTheme: prismThemes.vsDark,
        additionalLanguages: ['csharp', 'json', 'bash', 'powershell'],
      },
    }),
};

module.exports = config;

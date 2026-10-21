// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import starlightLlmsTxt from 'starlight-llms-txt';

// Cloudflare Web Analytics is cookie-free; it is on only when the deploy passes a beacon token.
const beacon = process.env.PUBLIC_CF_BEACON_TOKEN;

const site = process.env.DOCS_SITE ?? 'https://deedbox-docs.pages.dev';

export default defineConfig({
  site,
  integrations: [
    starlight({
      title: 'Deedbox',
      description: 'Event-source part of your app. Postgres or SQL Server. EF Core, Dapper, or neither.',
      social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/alternayte/deedbox' }],
      editLink: { baseUrl: 'https://github.com/alternayte/deedbox/edit/main/site/' },
      components: { SiteTitle: './src/components/SiteTitle.astro', Footer: './src/components/Footer.astro' },
      head: [
        // Starlight sets twitter:card to summary_large_image, so a page with no og:image shares as a blank card.
        // public/og.png carries the product name and the one-line description on a flat background.
        { tag: 'meta', attrs: { property: 'og:image', content: new URL('/og.png', site).href } },
        { tag: 'meta', attrs: { property: 'og:image:width', content: '1200' } },
        { tag: 'meta', attrs: { property: 'og:image:height', content: '630' } },
        { tag: 'meta', attrs: { property: 'og:image:alt', content: 'Deedbox. Event-source part of your app. Postgres or SQL Server. EF Core, Dapper, or neither.' } },
        ...(beacon
          ? [{ tag: 'script', attrs: { defer: true, src: 'https://static.cloudflareinsights.com/beacon.min.js', 'data-cf-beacon': JSON.stringify({ token: beacon }) } }]
          : []),
      ],
      sidebar: [
        { label: 'Tutorials', items: [{ autogenerate: { directory: 'tutorials' } }] },
        { label: 'How-to guides', items: [{ autogenerate: { directory: 'how-to' } }] },
        { label: 'Concepts', items: [{ autogenerate: { directory: 'concepts' } }] },
        { label: 'Reference', items: [
          { slug: 'reference/api' },
          { slug: 'reference/configuration' },
          { slug: 'reference/schema' },
          { slug: 'reference/telemetry' },
          { slug: 'reference/cli' },
          { label: 'Errors', collapsed: true, items: [{ autogenerate: { directory: 'reference/errors' } }] },
        ] },
        { label: 'Operations', items: [{ autogenerate: { directory: 'operations' } }] },
      ],
      plugins: [starlightLlmsTxt({ projectName: 'Deedbox' })],
    }),
  ],
});

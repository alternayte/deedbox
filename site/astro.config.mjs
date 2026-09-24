// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import starlightLlmsTxt from 'starlight-llms-txt';

// Cloudflare Web Analytics is cookie-free; it is on only when the deploy passes a beacon token.
const beacon = process.env.PUBLIC_CF_BEACON_TOKEN;

export default defineConfig({
  site: process.env.DOCS_SITE ?? 'https://deedbox-docs.pages.dev',
  integrations: [
    starlight({
      title: 'Deedbox',
      description: 'Event-source part of your app. Postgres or SQL Server. EF Core, Dapper, or neither.',
      social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/alternayte/deedbox' }],
      editLink: { baseUrl: 'https://github.com/alternayte/deedbox/edit/main/site/' },
      components: { SiteTitle: './src/components/SiteTitle.astro' },
      head: beacon
        ? [{ tag: 'script', attrs: { defer: true, src: 'https://static.cloudflareinsights.com/beacon.min.js', 'data-cf-beacon': JSON.stringify({ token: beacon }) } }]
        : [],
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

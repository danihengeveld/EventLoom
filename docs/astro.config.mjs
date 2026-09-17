// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

// https://astro.build/config
export default defineConfig({
	integrations: [starlight({
		title: 'EventLoom',
		description: 'Event sourcing for .NET and EF Core.',
		sidebar: [
			{ label: 'Getting Started', items: [{ label: 'Introduction', slug: 'index' }, { label: 'Installation', slug: 'getting-started/installation' }] },
			{ label: 'Concepts', items: [{ label: 'Architecture', slug: 'concepts/architecture' }] },
			{ label: 'How-to guides', items: [{ label: 'Configure the EF Core event store', slug: 'guides/configure-ef-core' }, { label: 'Append and read events', slug: 'guides/append-and-read' }] },
			{ label: 'Reference', items: [{ autogenerate: { directory: 'reference' } }] },
		],
	})],
});

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
			{ label: 'Reference', items: [{ autogenerate: { directory: 'reference' } }] },
		],
	})],
});

// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

// https://astro.build/config
export default defineConfig({
	integrations: [starlight({
		title: 'EventLoom',
		description: 'Event sourcing for .NET and EF Core.',
		favicon: '/favicon.svg',
		logo: {
			src: './src/assets/eventloom-icon.svg',
			alt: 'EventLoom',
			replacesTitle: false,
		},
		sidebar: [
			{ label: 'Getting Started', items: [{ label: 'Introduction', slug: 'index' }, { label: 'Installation', slug: 'getting-started/installation' }, { label: 'Build your first aggregate', slug: 'getting-started/first-aggregate' }] },
			{ label: 'Concepts', items: [{ label: 'Architecture', slug: 'concepts/architecture' }, { label: 'Aggregates and events', slug: 'concepts/aggregates-and-events' }, { label: 'Serialization and event evolution', slug: 'concepts/serialization-and-evolution' }, { label: 'Tenancy and ordering', slug: 'concepts/tenancy-and-ordering' }, { label: 'Testing strategy', slug: 'concepts/testing' }] },
			{ label: 'How-to guides', items: [{ label: 'Configure the event store', slug: 'guides/configure-ef-core' }, { label: 'Append and read events', slug: 'guides/append-and-read' }, { label: 'Snapshots', slug: 'guides/snapshots' }, { label: 'Projections', slug: 'guides/projections' }, { label: 'Outbox and application integration', slug: 'guides/outbox' }, { label: 'Observability', slug: 'guides/observability' }, { label: 'Production deployment', slug: 'guides/production-deployment' }, { label: 'Ordering API sample', slug: 'guides/ordering-api' }] },
			{ label: 'Reference', items: [{ autogenerate: { directory: 'reference' } }] },
		],
	})],
});

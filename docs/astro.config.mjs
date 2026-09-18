// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import mermaid from 'astro-mermaid';

// https://astro.build/config
export default defineConfig({
	integrations: [
		mermaid({ enableLog: false }),
		starlight({
			title: 'EventLoom',
			description: 'Event sourcing for .NET and EF Core.',
			favicon: '/favicon.svg',
			logo: {
				src: './src/assets/eventloom-icon.svg',
				alt: 'EventLoom',
				replacesTitle: false,
			},
			sidebar: [
			{
				label: 'Start here',
				items: [
					{ label: 'Overview', slug: 'index' },
					{ label: 'Installation', slug: 'getting-started/installation' },
					{ label: 'Build your first aggregate', slug: 'getting-started/first-aggregate' },
				],
			},
			{
				label: 'Core concepts',
				items: [
					{ label: 'Architecture', slug: 'concepts/architecture' },
					{ label: 'Aggregates and events', slug: 'concepts/aggregates-and-events' },
					{ label: 'Tenancy and ordering', slug: 'concepts/tenancy-and-ordering' },
					{ label: 'Delivery model', slug: 'concepts/delivery-model' },
					{ label: 'Serialization and event evolution', slug: 'concepts/serialization-and-evolution' },
				],
			},
			{
				label: 'Guides',
				items: [
					{ label: 'Configure the event store', slug: 'guides/configure-ef-core' },
					{ label: 'Append and read events', slug: 'guides/append-and-read' },
					{ label: 'Use snapshots', slug: 'guides/snapshots' },
					{ label: 'Build projections', slug: 'guides/projections' },
					{ label: 'Publish integration messages', slug: 'guides/outbox' },
					{ label: 'Test an EventLoom application', slug: 'concepts/testing' },
					{ label: 'Add observability', slug: 'guides/observability' },
					{ label: 'Deploy and recover', slug: 'guides/production-deployment' },
					{ label: 'Explore the Ordering API sample', slug: 'guides/ordering-api' },
				],
			},
			{
				label: 'Reference',
				items: [
					{ label: 'Packages and compatibility', slug: 'reference/packages' },
					{ label: 'Configuration', slug: 'reference/configuration' },
					{ label: 'Guarantees and operational APIs', slug: 'reference/guarantees' },
					{ label: 'Glossary', slug: 'reference/glossary' },
				],
			},
			],
		}),
	],
});

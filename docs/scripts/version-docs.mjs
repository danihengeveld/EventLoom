import { cp, mkdtemp, readFile, readdir, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const docsDirectory = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const contentDirectory = path.join(docsDirectory, 'src', 'content', 'docs');
const manifestPath = path.join(docsDirectory, 'src', 'data', 'docs-versions.json');
const [command, version] = process.argv.slice(2);

if (!['snapshot', 'validate'].includes(command) || !version) {
    throw new Error('Usage: node docs/scripts/version-docs.mjs <snapshot|validate> <semver>');
}

const releaseLine = getReleaseLine(version);

if (command === 'snapshot') {
    await createSnapshot(releaseLine, version);
} else {
    await validateSnapshot(releaseLine);
}

function getReleaseLine(value) {
    const identifier = String.raw`(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)`;
    const match = new RegExp(
        String.raw`^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-${identifier}(?:\.${identifier})*)?$`,
    ).exec(value);
    if (!match) {
        throw new Error(`Unsupported SemVer version: ${value}`);
    }

    const major = Number(match[1]);
    return major === 0 ? `v0.${Number(match[2])}` : `v${major}`;
}

async function createSnapshot(line, releaseVersion) {
    const targetDirectory = path.join(contentDirectory, line);
    const temporaryRoot = await mkdtemp(path.join(tmpdir(), 'eventloom-docs-'));
    const temporarySnapshot = path.join(temporaryRoot, line);

    try {
        await cp(contentDirectory, temporarySnapshot, {
            recursive: true,
            filter: (source) => {
                const relative = path.relative(contentDirectory, source);
                const topLevel = relative.split(path.sep)[0];
                return relative === '' || !/^v\d+(?:\.\d+)?$/.test(topLevel);
            },
        });
        await rewriteSnapshot(temporarySnapshot, temporarySnapshot, line);
        await rm(targetDirectory, { recursive: true, force: true });
        await cp(temporarySnapshot, targetDirectory, { recursive: true });
        await updateManifest(line, releaseVersion);
    } finally {
        await rm(temporaryRoot, { recursive: true, force: true });
    }

    console.log(`Updated documentation snapshot ${line}.`);
}

async function rewriteSnapshot(snapshotRoot, directory, line) {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
        const entryPath = path.join(directory, entry.name);
        if (entry.isDirectory()) {
            await rewriteSnapshot(snapshotRoot, entryPath, line);
        } else if (/\.(md|mdx)$/.test(entry.name)) {
            const content = await readFile(entryPath, 'utf8');
            const relative = path.relative(snapshotRoot, entryPath).replaceAll(path.sep, '/');
            const contentSlug = relative.replace(/\.(md|mdx)$/, '').replace(/\/index$/, '');
            const slug = contentSlug === 'index' ? line : `${line}/${contentSlug}`;
            const rewrittenLinks = content
                .replaceAll('](/', `](/${line}/`)
                .replaceAll('href="/', `href="/${line}/`);
            const rewritten = rewrittenLinks.replace(/^---\n/, `---\nslug: ${slug}\n`);
            await writeFile(entryPath, rewritten);
        }
    }
}

async function updateManifest(line, releaseVersion) {
    const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
    const entry = { id: line, label: line, path: `/${line}/`, version: releaseVersion };
    manifest.versions = [
        entry,
        ...manifest.versions.filter((versionEntry) => versionEntry.id !== line),
    ].sort((left, right) => right.id.localeCompare(left.id, undefined, { numeric: true }));
    await writeFile(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
}

async function validateSnapshot(line) {
    const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
    const entry = manifest.versions.find((versionEntry) => versionEntry.id === line);
    if (!entry || entry.path !== `/${line}/` || entry.version !== version) {
        throw new Error(`Documentation manifest is not prepared for ${version} on release line ${line}.`);
    }

    const snapshot = await stat(path.join(contentDirectory, line)).catch(() => undefined);
    if (!snapshot?.isDirectory()) {
        throw new Error(`Documentation snapshot ${line} does not exist.`);
    }

    console.log(`Documentation snapshot ${line} is ready for ${version}.`);
}

import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const docsRoot = path.join(repoRoot, 'docs');
const docsIndex = path.join(docsRoot, 'README.md');
const ignoredDirectoryNames = new Set([
  '.git',
  '.cursor',
  '.codex',
  '.tmp',
  'artifacts',
  'bin',
  'dist',
  'node_modules',
  'obj',
]);
const allowedSections = new Set([
  'adr',
  'architecture',
  'components',
  'development',
  'operations',
  'history',
  'plans',
  'reference',
]);

const failures = [];
const documentationFiles = listMarkdownFiles(docsRoot);
const allCheckedFiles = listMarkdownFiles(repoRoot);
const anchorCache = new Map();

if (!fs.existsSync(docsIndex)) {
  failures.push('docs/README.md is required as the documentation entry point.');
}

for (const filePath of documentationFiles) {
  const relative = slash(path.relative(docsRoot, filePath));
  const segments = relative.split('/');
  if (segments.length === 1 && !['README.md', 'getting-started.md'].includes(relative)) {
    failures.push(`${slash(path.relative(repoRoot, filePath))}: documentation must live in a named section.`);
  }
  if (segments.length > 1 && !allowedSections.has(segments[0])) {
    failures.push(`${slash(path.relative(repoRoot, filePath))}: unrecognized documentation section '${segments[0]}'.`);
  }

  if (segments[0] === 'plans') {
    const text = fs.readFileSync(filePath, 'utf8');
    for (const field of ['**Status:** Active', '**Owner:**', '**Activation condition:**']) {
      if (!text.includes(field)) {
        failures.push(`${slash(path.relative(repoRoot, filePath))}: active plans must include '${field}'.`);
      }
    }
  }
}

const graph = new Map();
for (const filePath of allCheckedFiles) {
  const text = fs.readFileSync(filePath, 'utf8');
  for (const reference of text.matchAll(/\bdocs\/[A-Za-z0-9._/-]+\.md\b/gu)) {
    const target = path.resolve(repoRoot, reference[0]);
    if (!fs.existsSync(target)) {
      failures.push(`${slash(path.relative(repoRoot, filePath))}: stale repository documentation reference '${reference[0]}'.`);
    }
  }

  const links = extractLocalLinks(filePath);
  const resolvedDocuments = [];

  for (const link of links) {
    const resolved = resolveLocalLink(filePath, link);
    if (!resolved) continue;

    if (!fs.existsSync(resolved)) {
      failures.push(`${slash(path.relative(repoRoot, filePath))}: broken link '${link}'.`);
      continue;
    }

    const fragment = link.split('#').slice(1).join('#').split('?')[0];
    if (fragment && resolved.toLowerCase().endsWith('.md')) {
      let anchor;
      try { anchor = decodeURIComponent(fragment).toLowerCase(); }
      catch { anchor = fragment.toLowerCase(); }
      if (!anchorCache.has(resolved)) anchorCache.set(resolved, collectMarkdownAnchors(resolved));
      if (!anchorCache.get(resolved).has(anchor)) {
        failures.push(`${slash(path.relative(repoRoot, filePath))}: missing in-page anchor '${fragment}' in '${slash(path.relative(repoRoot, resolved))}'.`);
      }
    }

    if (resolved.toLowerCase().endsWith('.md') && isWithin(resolved, docsRoot)) {
      resolvedDocuments.push(path.normalize(resolved));
    }
  }

  graph.set(path.normalize(filePath), resolvedDocuments);
}

if (fs.existsSync(docsIndex)) {
  const reachable = new Set();
  const pending = [path.normalize(docsIndex)];
  while (pending.length > 0) {
    const current = pending.pop();
    if (!current || reachable.has(current)) continue;
    reachable.add(current);
    for (const target of graph.get(current) ?? []) pending.push(target);
  }

  for (const filePath of documentationFiles) {
    if (!reachable.has(path.normalize(filePath))) {
      failures.push(`${slash(path.relative(repoRoot, filePath))}: orphaned from docs/README.md.`);
    }
  }
}

if (failures.length > 0) {
  console.error('Documentation validation failed:');
  for (const failure of failures) console.error(`- ${failure}`);
  process.exit(1);
}

console.log(`Documentation validation passed (${documentationFiles.length} docs).`);

function listMarkdownFiles(root) {
  const files = [];
  for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
    const fullPath = path.join(root, entry.name);
    if (entry.isDirectory() && !ignoredDirectoryNames.has(entry.name)) {
      files.push(...listMarkdownFiles(fullPath));
    }
    else if (entry.isFile() && entry.name.toLowerCase().endsWith('.md')) files.push(fullPath);
  }
  return files.sort();
}

function extractLocalLinks(filePath) {
  const text = fs.readFileSync(filePath, 'utf8');
  const links = [];
  const expression = /!?\[[^\]]*\]\(([^)]+)\)/gu;
  for (const match of text.matchAll(expression)) {
    let target = match[1].trim();
    if (target.startsWith('<') && target.endsWith('>')) target = target.slice(1, -1);
    target = target.replace(/\s+["'][^"']*["']$/u, '');
    if (!target || /^[a-z][a-z0-9+.-]*:/iu.test(target)) continue;
    links.push(target);
  }
  return links;
}

function resolveLocalLink(sourcePath, link) {
  const withoutFragment = link.split('#', 1)[0].split('?', 1)[0];
  if (!withoutFragment) return sourcePath;
  try {
    return path.resolve(path.dirname(sourcePath), decodeURIComponent(withoutFragment));
  } catch {
    failures.push(`${slash(path.relative(repoRoot, sourcePath))}: invalid encoded link '${link}'.`);
    return null;
  }
}

function collectMarkdownAnchors(filePath) {
  const text = fs.readFileSync(filePath, 'utf8');
  const anchors = new Set();
  const occurrences = new Map();
  for (const match of text.matchAll(/^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$/gmu)) {
    const title = match[1]
      .replace(/!?(\[([^\]]*)\])\([^)]*\)/gu, '$2')
      .replace(/<[^>]*>/gu, '')
      .replace(/`([^`]*)`/gu, '$1')
      .replace(/[\*_~]/gu, '')
      .trim()
      .toLowerCase();
    const base = title.replace(/[^\p{L}\p{N}\s-]/gu, '').replace(/\s+/gu, '-');
    const count = occurrences.get(base) ?? 0;
    occurrences.set(base, count + 1);
    anchors.add(count ? `${base}-${count}` : base);
  }
  for (const match of text.matchAll(/\bid=["']([^"']+)["']/giu)) anchors.add(match[1].toLowerCase());
  return anchors;
}

function isWithin(filePath, root) {
  const relative = path.relative(root, filePath);
  return relative !== '' && !relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative);
}

function slash(value) {
  return value.replaceAll('\\', '/');
}

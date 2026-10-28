#!/usr/bin/env node
import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';

const repoRoot = process.cwd();
const scanRoots = [
  'scripts',
  'src/backend/Kautuka.Gateway',
  'src/frontend/web',
  'src/tests',
];

const ignoredSegments = new Set(['bin', 'obj', 'node_modules', 'dist', 'coverage']);
const allowedTermFiles = new Set([
  path.normalize('scripts/validate-runtime-lineage.mjs'),
  path.normalize('src/backend/Kautuka.Gateway/Serialization/GatewayRuntimePayloadGuard.cs'),
  // Chat UI v2 stream consumer (not a truth projector).
  path.normalize('src/frontend/web/src/features/chat/api/chatStream.ts'),
]);
const forbiddenRuntimeFields = ['plannedStepId', 'deviationReason', 'unplanned'];
const forbiddenRunsDbTerms = [
  'runs.db',
  'run_timeline_events',
  'session_view_cache',
  'KautukaRunsDatabase',
  'RunTimelineWriter',
  'SqliteRunTimelineStore',
  'RunStartCoordinator',
  'RunEngine',
];
const forbiddenProjectionPaths = [
  '/api/copilotkit',
  '/api/agui',
  '/api/agui/',
  '/api/sessions/{sessionId}/view',
  'fetchChatSessionView',
  'UpdateFromAguiPayload',
  'AguiRawEventDebugHub',
  'AguiRunStreamHub',
  'RuntimeDebugPanel',
  'RawRuntimeDebugPanel',
  'RuntimeDebugSidePanel',
  'DebugSidePanel',
  'RuntimeInspectorPanel',
  'ProjectRuntimeActivityEntries',
  'ProjectRuntimeSnapshots',
  'SessionMessageTurnProjector',
];

const sourceExtensions = new Set([
  '.cs',
  '.ts',
  '.tsx',
  '.js',
  '.jsx',
  '.mjs',
  '.cjs',
  '.json',
]);

const findings = [];

for (const root of scanRoots) {
  await walk(path.join(repoRoot, root));
}

if (findings.length > 0) {
  console.error('Runtime lineage validation failed:');
  for (const finding of findings) {
    console.error(`- ${path.relative(repoRoot, finding.file)}:${finding.line}: ${finding.term}`);
  }
  process.exit(1);
}

console.log('Runtime lineage validation passed.');

async function walk(currentPath) {
  let entries;
  try {
    entries = await readdir(currentPath, { withFileTypes: true });
  } catch {
    return;
  }

  for (const entry of entries) {
    const entryPath = path.join(currentPath, entry.name);
    if (entry.isDirectory()) {
      if (!ignoredSegments.has(entry.name)) {
        await walk(entryPath);
      }
      continue;
    }

    if (!sourceExtensions.has(path.extname(entry.name))) {
      continue;
    }

    await scanFile(entryPath);
  }
}

async function scanFile(file) {
  if (allowedTermFiles.has(path.relative(repoRoot, file))) {
    return;
  }

  const text = await readFile(file, 'utf8');
  const lines = text.split(/\r?\n/);
  const relativeFile = path.relative(repoRoot, file);
  if (relativeFile.startsWith(path.normalize('src/frontend/web/src')) &&
      !relativeFile.includes(`${path.sep}test${path.sep}`)) {
    const aguiLifecycleTerms = ['MESSAGES_SNAPSHOT', 'RUN_STARTED', 'RUN_FINISHED', 'TOOL_CALL_START'];
    const matchedTerms = aguiLifecycleTerms.filter((term) => text.includes(term));
    if (matchedTerms.length >= 2) {
      const line = lines.findIndex((lineText) => matchedTerms.some((term) => lineText.includes(term))) + 1;
      findings.push({ file, line, term: 'frontend protocol lifecycle projection' });
    }
  }

  const terms = [
    ...forbiddenRuntimeFields,
    ...forbiddenRunsDbTerms,
    ...forbiddenProjectionPaths,
  ];

  lines.forEach((lineText, index) => {
    for (const term of terms) {
      if (lineText.includes(term)) {
        findings.push({ file, line: index + 1, term });
      }
    }
  });
}

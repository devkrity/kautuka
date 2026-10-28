#!/usr/bin/env node
/**
 * Fail when runtime databases, pid files, or other local leftovers are tracked.
 */
import { execFileSync } from 'node:child_process';

const forbidden = [
  /(^|\/)[^/]+\.(?:db|sqlite|sqlite3)(?:-(?:wal|shm|journal)|\.bak(?:-[^/]*)?|\.backup(?:-[^/]*)?)?$/i,
  /\.pid$/i,
  /^data\//i,
  /^session\.json$/i,
  /^\.kautuka\//,
  /^\.pnpm-store\//,
  /^\.codex\//,
  /^codebase-size\.csv$/,
  /^config\.json$/,
  /^vendor\/narad\//,
  /^tests\/channel-acceptance\/run-.*\.ps1$/,
  /^tests\/channel-acceptance\/full-suite-latest\.txt$/,
];

const tracked = execFileSync('git', ['ls-files', '-z'], { encoding: 'utf8' })
  .split('\0')
  .filter(Boolean);

const violations = tracked.filter((file) =>
  forbidden.some((pattern) => pattern.test(file.replaceAll('\\', '/')))
);

if (violations.length > 0) {
  console.error('Tracked hygiene ratchet failed. Untrack these paths:');
  for (const file of violations) {
    console.error(`- ${file}`);
  }
  process.exit(1);
}

console.log('Tracked hygiene ratchet passed.');

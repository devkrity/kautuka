#!/usr/bin/env node
import { readFileSync } from 'node:fs';

const mode = process.argv[2];
let payload;
try { payload = JSON.parse(readFileSync(0, 'utf8')); } catch { throw new Error('Invalid release policy input.'); }

if (mode === 'protected-branch') {
  if (!Array.isArray(payload)) throw new Error('Expected effective branch rules.');
  const types = new Set(payload.map((rule) => rule.type));
  const contexts = new Set(payload.filter((rule) => rule.type === 'required_status_checks')
    .flatMap((rule) => rule.parameters?.required_status_checks?.map((check) => check.context) ?? []));
  if (!types.has('pull_request') || !types.has('non_fast_forward')
    || !['Format, Build, Test', 'Gitleaks', 'DCO'].every((check) => contexts.has(check))) {
    throw new Error('Release branch is missing required protection or checks.');
  }
} else if (mode === 'latest-preview' || mode === 'latest-preview-of-version') {
  if (!Array.isArray(payload) || payload.some((page) => !Array.isArray(page))) throw new Error('Expected paginated releases.');
  const version = mode === 'latest-preview-of-version' ? process.argv[3] : undefined;
  if (mode === 'latest-preview-of-version' && !/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(version ?? '')) throw new Error('Expected strict Stable version.');
  const releases = payload.flat();
  if (releases.length > 1000) throw new Error('Release discovery exceeds its supported limit.');
  const pattern = /^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)-preview\.(0|[1-9]\d*)$/;
  const candidates = releases.flatMap((release) => {
    const match = typeof release.tag_name === 'string' ? pattern.exec(release.tag_name) : null;
    if (!match || (version && match.slice(1, 4).join('.') !== version)
      || release.draft !== false || release.prerelease !== true || !release.published_at) return [];
    return [{ tag: release.tag_name, version: match.slice(1).map(BigInt), immutable: release.immutable }];
  });
  candidates.sort((left, right) => {
    for (let i = 0; i < left.version.length; i++) {
      if (left.version[i] !== right.version[i]) return left.version[i] > right.version[i] ? -1 : 1;
    }
    return 0;
  });
  if (candidates.length > 1 && candidates[0].tag === candidates[1].tag) throw new Error('Duplicate published Preview tag.');
  if (candidates[0] && candidates[0].immutable !== true) throw new Error('Latest published Preview must be immutable.');
  if (candidates[0]) process.stdout.write(candidates[0].tag);
} else {
  throw new Error('Unknown release policy mode.');
}

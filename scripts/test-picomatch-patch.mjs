import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const frontendRequire = createRequire(path.join(root, 'src/frontend/package.json'));
const vitestRequire = createRequire(frontendRequire.resolve('vitest/package.json'));
const modulePath = process.env.PICOMATCH_TEST_PATH || vitestRequire.resolve('picomatch');
const program = `
const assert = require('node:assert/strict');
const pm = require(${JSON.stringify(modulePath)});
const patterns = ['+(*)','+(*a)','*(*a)','+(*)b','+(a*)b','+(a*b)','*(a*b)c','+(*.js)x','+(?*)','+([a-z]*)','+(@(*a))','+(@(*)a)','*(@(a*))b'];
for (const options of [{}, {dot:true}, {windows:true}, {bash:true}, {nocase:true}, {capture:true}]) {
  for (const pattern of patterns) {
    for (const input of ['a'.repeat(50000) + '/', 'a'.repeat(50000) + '!']) pm(pattern, options)(input);
  }
}
assert(pm.isMatch('aaa', '+(*a)'));
assert(!pm.isMatch('ab', '+(*a)'));
assert(pm.isMatch('abab', '+(a*b)'));
assert(!pm.isMatch('a/a', '+(*a)'));
assert(pm.isMatch('src/a/src/b/', '+(src/*/)'));
`;
const result = spawnSync(process.execPath, ['-e', program], { timeout: 15000, encoding: 'utf8' });
assert.equal(result.error, undefined, `picomatch regression check timed out: ${result.error}`);
assert.equal(result.status, 0, result.stderr);
console.log('Patched picomatch handles advisory patterns and preserves representative matches.');

#!/usr/bin/env node
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { EventEmitter } from 'node:events';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;

// Execute the production scripts with an in-memory filesystem and a compiler
// boundary that stops before any external program runs. No real cached bundle,
// build output, package manager or platform setting is modified by these tests.
async function stage(role, scenario) {
  const file = path.join(root, 'scripts', `stage-${role}-mcpb.mjs`);
  const source = readFileSync(file, 'utf8')
    .replace(/^#!.*\n/, '')
    .replace(/^import .*;\r?\n/gm, '')
    .replaceAll('import.meta.url', 'file');
  const execute = new AsyncFunction('file', 'spawn', 'createHash', 'existsSync', 'mkdirSync',
    'readdirSync', 'readFileSync', 'rmSync', 'statSync', 'copyFileSync', 'writeFileSync',
    'path', 'fileURLToPath', 'process', source);
  const bundle = Buffer.from('previously packed fixture');
  const hash = createHash('sha256').update(bundle).digest('hex');
  const mutations = [];
  const commands = [];
  const cached = new Error('cache hit');
  const compiler = new Error('compiler boundary');
  const processFixture = {
    platform: scenario === 'windows' ? 'win32' : 'linux', arch: 'x64', env: {},
    stdout: { write() {} }, exit(code) { assert.equal(code, 0); throw cached; },
  };
  const read = name => {
    if (name.endsWith('manifest.json')) return '{"version":"1.0.0"}';
    if (name.endsWith('.native-aot.json')) {
      if (scenario === 'old-jit') throw new Error('ENOENT');
      return JSON.stringify({ schemaVersion: 1, publication: 'linux-x64-native-aot',
        bundleSha256: scenario === 'tampered-aot' ? 'incorrect' : hash });
    }
    return bundle;
  };
  const spawn = (command, args) => {
    commands.push({ command, args });
    const child = new EventEmitter();
    queueMicrotask(() => child.emit('error', compiler));
    return child;
  };
  let failure;
  try {
    await execute(file, spawn, createHash, () => true, name => mutations.push(name),
      () => [], read, name => mutations.push(name), () => ({ mtimeMs: 10 }),
      name => mutations.push(name), name => mutations.push(name), path, value => value, processFixture);
  } catch (error) { failure = error; }
  return { failure, cached, compiler, mutations, commands };
}

for (const role of ['m365-mail', 'google-mail']) {
  test(`${role}: Windows cannot reuse even an AOT cache or cross-publish`, async () => {
    const result = await stage(role, 'windows');
    assert.match(result.failure.message, /Linux x64 Native AOT tooling is required/);
    assert.deepEqual(result.commands, []);
    assert.deepEqual(result.mutations, []);
  });
  test(`${role}: matching AOT provenance and bundle hash permit reuse`, async () => {
    const result = await stage(role, 'valid-aot');
    assert.equal(result.failure, result.cached);
    assert.deepEqual(result.commands, []);
    assert.deepEqual(result.mutations, []);
  });
  for (const scenario of ['old-jit', 'tampered-aot']) {
    test(`${role}: ${scenario} cache requires a fresh Native AOT publication`, async () => {
      const result = await stage(role, scenario);
      assert.equal(result.failure, result.compiler);
      assert.equal(result.commands.length, 1);
      assert.equal(result.commands[0].command, 'dotnet');
      assert.ok(result.commands[0].args.includes('-p:PublishAot=true'));
      assert.ok(!result.commands[0].args.includes('-p:PublishAot=false'));
      assert.ok(result.mutations.some(name => name.endsWith('.native-aot.json')));
      assert.ok(result.mutations.some(name => name.endsWith(`${role}-linux-x64`)));
    });
  }
}

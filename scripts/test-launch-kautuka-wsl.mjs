import assert from 'node:assert/strict';
import test from 'node:test';
import { createServer } from 'node:net';
import { assertOwnership, parseOptions, requireFreePort, workspaceIdentity } from './launch-kautuka-wsl.mjs';

test('development launcher cannot target production, acceptance or Podman distros', () => {
  for (const distro of ['Ubuntu', 'KautukaTestFedora44', 'podman-machine-default', '../KautukaDev', 'KautukaDev;echo']) {
    assert.throws(() => parseOptions(['--distro', distro]), /cannot be adopted/);
  }
  assert.equal(parseOptions(['--distro', 'KautukaDev-work']).distro, 'KautukaDev-work');
});

test('default launch is isolated and skip-build does not select host mode', () => {
  assert.deepEqual(parseOptions([]), { distro: 'KautukaDev', port: 20229, skipBuild: false, stop: false, help: false, service: null, action: null });
  assert.equal(parseOptions(['--skip-build']).skipBuild, true);
  assert.equal(parseOptions([], 'KautukaDev-0123abcd').distro, 'KautukaDev-0123abcd');
  assert.throws(() => parseOptions(['--unknown']), /Unknown option/);
  assert.throws(() => parseOptions(['--host']), /Unknown option/);
  for (const port of ['0', '22', '65536', 'NaN']) assert.throws(() => parseOptions(['--port', port]), /Invalid --port/);
});

test('individual service actions are explicit and cannot select arbitrary containers or stack modes', () => {
  assert.equal(parseOptions(['--service', 'webhost']).action, 'restart');
  assert.equal(parseOptions(['--service', 'frontend', '--action', 'rebuild']).service, 'frontend');
  for (const args of [['--service', 'egress-envoy'], ['--service'], ['--action', 'restart'], ['--service', 'gateway', '--action', 'delete'], ['--service', 'gateway', '--stop'], ['--service', 'gateway', '--skip-build']]) {
    assert.throws(() => parseOptions(args));
  }
});

test('an existing unmarked distro or different checkout is never adopted', () => {
  const identity = workspaceIdentity('D:\\Sources\\devkrity\\kautuka');
  assert.equal(identity, workspaceIdentity('d:/Sources/devkrity/kautuka'));
  assertOwnership(JSON.stringify({ schemaVersion: 1, workspaceId: identity }), identity);
  assert.throws(() => assertOwnership('', identity), /no valid/);
  assert.throws(() => assertOwnership(JSON.stringify({ schemaVersion: 1, workspaceId: 'another' }), identity), /another checkout/);
});

for (const host of ['127.0.0.1', '::1']) {
  test(`an existing ${host} listener is not adopted or displaced`, async t => {
    const server = createServer();
    try {
      await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, host, resolve); });
      await assert.rejects(requireFreePort(server.address().port), /already in use/);
    } catch (error) {
      if (host === '::1' && ['EAFNOSUPPORT', 'EADDRNOTAVAIL'].includes(error.code)) t.skip('IPv6 loopback unavailable');
      else throw error;
    } finally {
      await new Promise(resolve => server.close(resolve));
    }
  });
}

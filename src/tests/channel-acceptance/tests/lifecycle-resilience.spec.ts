import { createServer } from 'node:http';
import { resolve } from 'node:path';
import { expect, test } from '@playwright/test';
import { OwnedStackLifecycle, type ProcessSpec } from '../src/lifecycle.js';

test('direct-child lifecycle restarts only a verified owned process and records generations', async () => {
  const simulatorPort = await availablePort();
  const servicePort = await availablePort();
  const root = resolve(process.cwd(), '../../..');
  const simulator = nodeHealthProcess('ProviderSimulator', simulatorPort, root);
  const service = nodeHealthProcess('Gateway', servicePort, root);
  const lifecycle = new OwnedStackLifecycle({
    baseUrl: service.readinessUrl!,
    readinessUrls: [simulator.readinessUrl!, service.readinessUrl!],
    reuseExistingStack: false,
    simulator,
    services: [service],
    workspaceRoot: resolve(root, '.artifacts/channel-acceptance/lifecycle-tests'),
  });

  try {
    await lifecycle.start();
    const original = lifecycle.processRecords().find((record) => record.name === 'Gateway');
    expect(original).toBeDefined();
    await expect(lifecycle.restartOwnedProcess('not-owned')).rejects.toThrow(/not an owned child/);

    const restarted = await lifecycle.restartOwnedProcess('Gateway');
    expect(restarted.generation).toBe(2);
    expect(restarted.pid).not.toBe(original!.pid);
    expect((await fetch(service.readinessUrl!)).ok).toBeTruthy();
    expect(lifecycle.processRecords().filter((record) => record.name === 'Gateway').map((record) => record.generation)).toEqual([1, 2]);
  } finally {
    await lifecycle.stop();
  }
});

test('owned lifecycle validates endpoint occupancy after preflight and spawns no colliding child', async () => {
  const port = await availablePort();
  const root = resolve(process.cwd(), '../../..');
  const readinessUrl = new URL(`http://127.0.0.1:${port}/health`);
  const blocker = createServer((_request, response) => response.end('occupied'));
  const blockerStarted = new Promise<void>((resolveStarted, rejectStarted) => {
    const timer = setTimeout(() => blocker.listen(port, '127.0.0.1', resolveStarted), 50);
    blocker.once('error', (error) => { clearTimeout(timer); rejectStarted(error); });
  });
  const lifecycle = new OwnedStackLifecycle({
    baseUrl: readinessUrl,
    readinessUrls: [readinessUrl],
    reuseExistingStack: false,
    preflight: [{ name: 'DelayedPreflight', command: process.execPath, args: ['-e', 'setTimeout(() => {}, 250)'], cwd: root }],
    simulator: nodeHealthProcess('ProviderSimulator', await availablePort(), root),
    services: [nodeHealthProcess('Gateway', port, root)],
    workspaceRoot: resolve(root, '.artifacts/channel-acceptance/lifecycle-tests'),
  });

  try {
    await expect(lifecycle.start()).rejects.toThrow(/already in use/);
    await blockerStarted;
    expect(lifecycle.processRecords()).toEqual([]);
  } finally {
    await new Promise<void>((resolveClosed) => blocker.close(() => resolveClosed()));
    await lifecycle.stop();
  }
});

function nodeHealthProcess(name: string, port: number, cwd: string): ProcessSpec {
  const readinessUrl = new URL(`http://127.0.0.1:${port}/health`);
  const script = `require('node:http').createServer((request,response)=>{response.statusCode=request.url==='/health'?200:404;response.end('ok');}).listen(${port},'127.0.0.1');`;
  return { name, command: process.execPath, args: ['-e', script], cwd, readinessUrl };
}

async function availablePort(): Promise<number> {
  const server = createServer();
  await new Promise<void>((resolveListening, rejectListening) => {
    server.once('error', rejectListening);
    server.listen(0, '127.0.0.1', resolveListening);
  });
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error('Expected an allocated TCP port.');
  await new Promise<void>((resolveClosed, rejectClosed) => server.close((error) => error ? rejectClosed(error) : resolveClosed()));
  return address.port;
}

import { expect, test } from '@playwright/test';
import { randomBytes } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import type { Server } from 'node:http';
import { sendRestartMailPrompt, startRestartModel } from './broker-restart-model.js';
import { startRestartMailFlow } from './broker-restart-flow.js';
import { resolve } from 'node:path';
import { OwnedStackLifecycle } from '../src/lifecycle.js';
import { createResilienceExecution } from '../src/resilience-executor.js';
import { removeOwnedState } from '../src/restart-evidence.js';

// Only the Playwright client ignores the local development certificate; the
// Gateway uses its normal TLS validation against the trusted OS certificate.
test.use({ screenshot: 'off', trace: 'off', video: 'off', ignoreHTTPSErrors: true });

for (const boundary of ['activation', 'refresh'] as const) test(`Gateway and CredentialsHost restart after committed mailbox ${boundary} acknowledgement`, async ({ request }) => {
  test.skip(process.env.KAUTUKA_TEST_GATED_BROKER !== 'https://localhost:28788', 'Requires the isolated local gated broker.');
  test.setTimeout(180_000);
  const broker = 'https://localhost:28788';
  const execution = createResilienceExecution();
  const services = execution.stack.services.map(service => ({ ...service, environment: {
    ...service.environment, Kautuka__Transport: 'tcp', Kautuka__ConnectorAuth__Host: broker,
    Kautuka__Auth__Google__ClientId: 'local-client',
  } }));
  const lifecycle = new OwnedStackLifecycle({ ...execution.stack, services, preflight: [] });
  const base = execution.stack.baseUrl;
  let pending: Promise<unknown> | undefined;
  let modelServer: Server | undefined;
  let resetModel: (() => void) | undefined;
  try {
    await lifecycle.start();
    const token = (await readFile(resolve(execution.stateRoot, 'setup-token'), 'utf8')).trim();
    const password = randomBytes(24).toString('base64url');
    expect((await request.post(new URL('/api/auth/setup', base).toString(), {
      data: { username: 'restart-admin', password, setupToken: token },
    })).ok()).toBe(true);
    async function login() {
      const response = await request.post(new URL('/api/auth/login', base).toString(), { data: { username: 'restart-admin', password } });
      expect(response.ok()).toBe(true);
      return (await response.json()).accessToken as string;
    }
    let accessToken = await login();
    const gatewayHeaders = () => ({ authorization: `Bearer ${accessToken}`, origin: base.origin });
    const flow = await startRestartMailFlow(request, base, broker, gatewayHeaders());
    if (boundary === 'activation') {
      expect((await request.post(`${broker}/__test_ack_arm`, { data: { flowId: flow.flowId } })).status()).toBe(204);
      pending = request.post(new URL('/api/connectors/oauth/bound-complete', base).toString(), {
        headers: gatewayHeaders(), data: { state: flow.state, tabHandle: flow.tabHandle }, timeout: 60_000,
      }).catch(() => undefined);
    } else {
      const complete = await request.post(new URL('/api/connectors/oauth/bound-complete', base).toString(), {
        headers: gatewayHeaders(), data: { state: flow.state, tabHandle: flow.tabHandle },
      });
      expect(complete.ok()).toBe(true);
      expect((await complete.json()).status).toBe('connected');
      const model = await startRestartModel(request, base, gatewayHeaders());
      modelServer = model.server;
      resetModel = model.reset;
      expect((await request.post(`${broker}/__test_ack_arm`, { data: { flowId: flow.flowId, kind: 'refresh' } })).status()).toBe(204);
      await sendRestartMailPrompt(request, base, gatewayHeaders());
      await expect.poll(async () => (await (await request.get(`${broker}/__test_refresh_started`)).json()).refreshStarted,
        { timeout: 60_000, intervals: [200] }).toBe(true);
      expect((await request.post(`${broker}/__test_refresh_release`)).status()).toBe(204);
    }
    await expect.poll(async () => (await (await request.get(`${broker}/__test_ack_status`)).json()).ackCommitted,
      { timeout: 20_000, intervals: [100] }).toBe(true);
    const paused = await lifecycle.pauseOwnedProcess('Gateway');
    await lifecycle.restartOwnedProcess('CredentialsHost');
    expect((await request.post(`${broker}/__test_ack_release`)).status()).toBe(204);
    if (pending) await pending;
    pending = undefined;
    await lifecycle.resumeOwnedProcess(paused.spec, paused.generation + 1);
    accessToken = await login();
    const status = await request.get(new URL('/api/connectors/oauth', base).toString(), { headers: gatewayHeaders() });
    expect(status.ok()).toBe(true);
    const item = (await status.json()).connectors.find((value: { connectorId: string }) => value.connectorId === 'kautuka-google-mail');
    expect(item.pending).toBe(boundary === 'activation');
    expect(item.connected).toBe(boundary === 'refresh');
    if (boundary === 'refresh') {
      // A new tool call must first recover the saved acknowledgement and then
      // present the persisted replacement credential to the broker. Stop the
      // fake provider with an error before any token can reach a real mailbox.
      expect((await request.post(`${broker}/__test_refresh_rearm`)).status()).toBe(204);
      resetModel!();
      await sendRestartMailPrompt(request, base, gatewayHeaders());
      await expect.poll(async () => (await (await request.get(`${broker}/__test_refresh_started`)).json()).refreshStarted,
        { timeout: 60_000, intervals: [200] }).toBe(true);
      const recovered = await (await request.get(`${broker}/__test_ack_status`)).json();
      expect(recovered.acknowledgements).toBe(2);
      expect(recovered.refreshes).toBe(2);
      expect((await request.post(`${broker}/__test_refresh_release`)).status()).toBe(204);
    }
    const disconnected = await request.delete(new URL('/api/connectors/kautuka-google-mail/oauth', base).toString(), { headers: gatewayHeaders() });
    expect(disconnected.ok()).toBe(true);
    expect((await disconnected.json()).disconnected).toBe(true);
    const counts = await (await request.get(`${broker}/__test_ack_status`)).json();
    expect(counts.acknowledgements).toBe(boundary === 'activation' ? 1 : 2);
    if (boundary === 'refresh') expect(counts.refreshes).toBe(2);
    expect(counts.disconnections).toBe(1);
  } finally {
    await request.post(`${broker}/__test_refresh_release`).catch(() => undefined);
    await request.post(`${broker}/__test_ack_release`).catch(() => undefined);
    if (pending) await pending;
    await lifecycle.stop();
    if (modelServer) await new Promise<void>((resolveClose, reject) => modelServer!.close(error => error ? reject(error) : resolveClose()));
    if (process.env.KAUTUKA_ACCEPTANCE_KEEP_WORKSPACE !== 'true') await removeOwnedState(execution.stateRoot);
  }
});

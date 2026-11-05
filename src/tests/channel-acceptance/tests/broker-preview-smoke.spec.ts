import { expect, test } from '@playwright/test';
import { randomBytes } from 'node:crypto';
import { access, readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { OwnedStackLifecycle } from '../src/lifecycle.js';
import { createResilienceExecution } from '../src/resilience-executor.js';
import { removeOwnedState } from '../src/restart-evidence.js';
import { assertPreviewFrontend, assertPreviewMailOperations, cleanupPreviewMail, configurePreviewAgent, waitForPreviewMail, writePreviewPassword } from './broker-preview-support.js';
import { restartPreviewMail } from './broker-preview-restart.js';
test.use({ screenshot: 'off', trace: 'off', video: 'off' });
test('isolated Gateway exercises social sign-in with the Cloudflare preview', async ({ request }) => {
  test.skip(process.env.KAUTUKA_TEST_BROKER_PREVIEW !== '1', 'Explicit preview opt-in is required.');
  if (process.env.KAUTUKA_TEST_SOCIAL_INTERACTIVE === '1' && process.env.KAUTUKA_TEST_MAIL_INTERACTIVE === '1')
    throw new Error('Run the social and mailbox interactive checks separately.');
  if (process.env.KAUTUKA_TEST_MAIL_INTERACTIVE === '1') test.setTimeout(1_800_000);
  const preview = 'https://broker-controls-kautuka-auth.aged-star-69fb.workers.dev';
  const execution = createResilienceExecution();
  const config = JSON.parse(await readFile(resolve(process.cwd(), '../../..', 'src/backend/Kautuka.Gateway/appsettings.Production.json'), 'utf8'));
  const googleClientId = config.Kautuka.Auth.Google.ClientId as string;
  const microsoftClientId = config.Kautuka.Auth.Microsoft.ClientId as string;
  const provider = process.env.KAUTUKA_TEST_SOCIAL_PROVIDER === 'microsoft' ? 'microsoft' : 'google';
  const services = execution.stack.services.map(service => ({ ...service, environment: {
    ...service.environment,
    Kautuka__Transport: 'tcp',
    Kautuka__ConnectorAuth__Host: preview,
    Kautuka__Auth__Google__ClientId: googleClientId,
    Kautuka__Auth__Microsoft__ClientId: microsoftClientId,
    ...(service.name === 'WebHost' ? { WebHost__ContentRoot: resolve(process.cwd(), '../../..', 'src/frontend/web/dist') } : {}),
  } }));
  const lifecycle = new OwnedStackLifecycle({ ...execution.stack, services, preflight: [] });
  let mailAccessToken: string | undefined;
  let mailPassword: string | undefined;
  let mailCleanupComplete = false;
  try {
    await lifecycle.start();
    await assertPreviewFrontend(request, execution.stack.baseUrl);
    const response = await request.post(new URL('/api/auth/external/bound-start', execution.stack.baseUrl).toString(), {
      headers: { origin: execution.stack.baseUrl.origin }, data: { provider },
    });
    expect(response.status()).toBe(200);
    const body = await response.json();
    expect(body.brokerOrigin).toBe(preview);
    expect(new URL(body.startUrl).origin).toBe(preview);
    if (process.env.KAUTUKA_TEST_SOCIAL_INTERACTIVE === '1') {
      console.log(`Isolated setup token file: ${resolve(execution.stateRoot, 'setup-token')}`);
      await expect.poll(async () => {
        const status = await request.get(new URL('/api/auth/setup/status', execution.stack.baseUrl).toString());
        return status.ok() && !(await status.json()).needsSetup;
      }, { timeout: 240_000, intervals: [1000] }).toBe(true);
      const { DatabaseSync } = await import('node:sqlite');
      const db = new DatabaseSync(resolve(execution.stateRoot, 'kautuka.db'), { readOnly: true });
      try { expect((db.prepare('SELECT COUNT(*) AS count FROM external_logins WHERE provider = ?').get(provider) as { count: number }).count).toBeGreaterThan(0); }
      finally { db.close(); }
    }
    if (process.env.KAUTUKA_TEST_MAIL_INTERACTIVE === '1') {
      const token = (await readFile(resolve(execution.stateRoot, 'setup-token'), 'utf8')).trim();
      const password = randomBytes(24).toString('base64url');
      mailPassword = password;
      const setup = await request.post(new URL('/api/auth/setup', execution.stack.baseUrl).toString(), {
        data: { username: 'preview-admin', password, setupToken: token },
      });
      expect(setup.ok()).toBe(true);
      const login = await request.post(new URL('/api/auth/login', execution.stack.baseUrl).toString(), {
        data: { username: 'preview-admin', password },
      });
      expect(login.ok()).toBe(true);
      let accessToken = (await login.json()).accessToken as string; mailAccessToken = accessToken;
      if (process.env.KAUTUKA_TEST_MAIL_OPERATIONS === '1')
        await configurePreviewAgent(request, execution.stack.baseUrl, accessToken);
      await writePreviewPassword(execution.stateRoot, password);
      await waitForPreviewMail(request, execution.stack.baseUrl, accessToken);
      if (process.env.KAUTUKA_TEST_MAIL_RECONNECT === '1') {
        expect(await cleanupPreviewMail(request, execution.stack.baseUrl, password)).toBe(false);
        const relogin = await request.post(new URL('/api/auth/login', execution.stack.baseUrl).toString(), { data: { username: 'preview-admin', password } });
        expect(relogin.ok()).toBe(true);
        accessToken = (await relogin.json()).accessToken as string;
        console.log('Both test mailboxes disconnected; reconnect both now.');
        await waitForPreviewMail(request, execution.stack.baseUrl, accessToken);
      }
      let journalWatermark = 0;
      if (process.env.KAUTUKA_TEST_MAIL_RESTART === '1') {
        const result = await restartPreviewMail(lifecycle, request, execution.stack.baseUrl, execution.stateRoot, password);
        mailAccessToken = result.token; journalWatermark = result.watermark;
      }
      if (process.env.KAUTUKA_TEST_MAIL_OPERATIONS === '1') {
        const marker = resolve(execution.stateRoot, 'mail-operations-finished');
        console.log(`Mail operation completion marker: ${marker}`);
        await expect.poll(async () => { try { await access(marker); return true; } catch { return false; } },
          { timeout: 900_000, intervals: [2000] }).toBe(true);
        await assertPreviewMailOperations(execution.stateRoot, journalWatermark);
      }
      const freshLogin = await request.post(new URL('/api/auth/login', execution.stack.baseUrl).toString(), {
        data: { username: 'preview-admin', password },
      });
      expect(freshLogin.ok()).toBe(true);
      const disconnectToken = (await freshLogin.json()).accessToken as string;
      for (const id of ['kautuka-google-mail', 'kautuka-m365-mail']) {
        const response = await request.delete(new URL(`/api/connectors/${id}/oauth`, execution.stack.baseUrl).toString(), {
          headers: { authorization: `Bearer ${disconnectToken}` },
        });
        expect(response.ok()).toBe(true);
        expect((await response.json()).disconnected).toBe(true);
      }
      mailCleanupComplete = true;
    }
  } finally {
    let cleanupFailed = false;
    if (mailAccessToken && !mailCleanupComplete && mailPassword) {
      cleanupFailed = await cleanupPreviewMail(request, execution.stack.baseUrl, mailPassword);
    }
    await lifecycle.stop();
    if (!cleanupFailed && process.env.KAUTUKA_ACCEPTANCE_KEEP_WORKSPACE !== 'true') await removeOwnedState(execution.stateRoot);
    if (cleanupFailed) throw new Error(`Preview cleanup failed; recover the retained test state at ${execution.stateRoot}.`);
  }
});

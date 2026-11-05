import { expect, test } from '@playwright/test';
import { createServer, type ViteDevServer } from 'vite';

const id = 'A'.repeat(43);
let server: ViteDevServer;
let origin: string;

test.beforeAll(async () => {
  server = await createServer({ server: { host: '127.0.0.1', port: 0 }, logLevel: 'silent' });
  await server.listen();
  const address = server.httpServer!.address();
  if (!address || typeof address === 'string') throw new Error('Vite did not start');
  origin = `http://127.0.0.1:${address.port}`;
});

test.afterAll(async () => { await server?.close(); });

test('mail sign-in binds the real popup before completing', async ({ page, context }) => {
  const calls: string[] = [];
  await context.route(`${origin}/test-harness`, route => route.fulfill({ contentType: 'text/html', body: `
    <button>Connect</button><output></output><script type="module">
    import { runBoundMailLogin } from '/src/features/auth/api/boundExternalLogin.ts';
    import { setAccessToken } from '/src/lib/authState.ts';
    setAccessToken('test-access', Date.now() + 3600000);
    window.addEventListener('message', e => { if (e.data?.nonce === '${'B'.repeat(43)}') document.body.dataset.untrustedSeen = 'yes'; });
    document.querySelector('button').onclick = () => { runBoundMailLogin('gmail').then(
      () => document.querySelector('output').textContent = 'connected',
      () => document.querySelector('output').textContent = 'failed'); };
    </script>` }));
  await context.route('https://auth.example/handoff*', route => route.fulfill({ contentType: 'text/html', body: `
    <iframe srcdoc="<script>parent.opener.postMessage({type:'kautuka-broker-candidate',flowId:'${id}',nonce:'${'B'.repeat(43)}'}, '${origin}');<\/script>"></iframe>
    <script>setTimeout(() => window.opener.postMessage({type:'kautuka-broker-candidate',flowId:'${id}',nonce:'${id}'}, '${origin}'), 300);</script>` }));
  await context.route(`${origin}/api/connectors/**`, async route => {
    const path = new URL(route.request().url()).pathname;
    calls.push(path);
    expect(route.request().headers().authorization).toBe('Bearer test-access');
    if (path.endsWith('/bound-begin')) return route.fulfill({ json: { startUrl: 'https://auth.example/handoff', brokerOrigin: 'https://auth.example', state: id, flowId: id, tabHandle: id } });
    if (path.endsWith('/bound-bind')) {
      expect(route.request().postDataJSON().nonce).toBe(id);
      return route.fulfill({ status: 204 });
    }
    if (path.endsWith('/bound-complete')) return route.fulfill({ json: { status: 'connected' } });
    throw new Error(`Unexpected request: ${path}`);
  });
  await page.goto(`${origin}/test-harness`);
  await page.getByRole('button', { name: 'Connect' }).click();
  await expect(page.locator('output')).toHaveText('connected');
  await expect(page.locator('body')).toHaveAttribute('data-untrusted-seen', 'yes');
  expect(calls).toEqual(['/api/connectors/gmail/oauth/bound-begin', '/api/connectors/oauth/bound-bind', '/api/connectors/oauth/bound-complete']);
});

test('administrator can queue and cancel a self-hosted broker change', async ({ page, context }) => {
  page.on('pageerror', error => { throw error; });
  const active = { origin: 'https://auth.kautuka.io', googleSocialClientId: 'hosted-google', microsoftSocialClientId: 'hosted-ms' };
  let pending: typeof active | null = null;
  let loseNextReply = false;
  await context.route(`${origin}/settings-harness`, route => route.fulfill({ contentType: 'text/html', body: `
    <div id="root"></div><script type="module">
    import RefreshRuntime from '/@react-refresh'; RefreshRuntime.injectIntoGlobalHook(window);
    window.$RefreshReg$ = () => {}; window.$RefreshSig$ = () => type => type;
    window.__vite_plugin_react_preamble_installed__ = true;
    </script><script type="module" src="/e2e/fixtures/authentication-broker-harness.tsx"></script>` }));
  await context.route(`${origin}/api/settings/system/authentication-broker`, async route => {
    if (route.request().method() === 'PUT') {
      const value = route.request().postDataJSON() as typeof active;
      pending = value.origin === active.origin ? null : value;
      if (loseNextReply) { loseNextReply = false; return route.abort('failed'); }
    }
    await route.fulfill({ json: { active, pending, switchPending: pending !== null } });
  });
  await page.goto(`${origin}/settings-harness`);
  await page.getByLabel('Broker origin').fill('https://selfhost.example');
  await expect(page.getByLabel('Google sign-in client ID')).toHaveValue('');
  await page.getByLabel('Google sign-in client ID').fill('selfhost-google');
  await page.getByRole('button', { name: 'Save broker settings' }).click();
  await expect(page.getByRole('status')).toContainText('Restart Kautuka');
  await page.getByRole('button', { name: 'Cancel pending change' }).click();
  await expect(page.getByLabel('Broker origin')).toHaveValue(active.origin);
  expect(pending).toBeNull();
  loseNextReply = true;
  await page.getByLabel('Broker origin').fill('https://selfhost.example');
  await page.getByLabel('Google sign-in client ID').fill('selfhost-google');
  await page.getByRole('button', { name: 'Save broker settings' }).click();
  await expect(page.getByRole('status')).toContainText('Settings for https://selfhost.example are pending. Restart Kautuka');
  await expect(page.getByRole('button', { name: 'Cancel pending change' })).toBeEnabled();
});

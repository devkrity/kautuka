import { expect, test } from '@playwright/test';

for (const width of [1280, 390]) {
  test(`network policy and approval controls work at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    const identity = { scopeType: 'agent', userId: 'owner', agentId: 'agent-1', role: 'connector', componentId: 'mail' };
    const state = {
      enabled: true, revision: 7, activeRevision: 7, activeVersion: 'accepted', activationState: 'active', lastError: null,
      rules: [], defaults: [], sources: [identity], requests: [{
        id: 'request-1', identity, destination: 'graph.microsoft.com', port: 443, attempts: 3, status: 'pending',
        createdAtUtc: '2026-11-07T00:00:00Z', lastSeenAtUtc: '2026-11-07T00:01:00Z',
      }],
    };
    const decisions: unknown[] = [];
    await page.route('**/api/**', async (route) => {
      const request = route.request();
      const path = new URL(request.url()).pathname;
      const json = (body: unknown) => route.fulfill({ json: body });
      if (path === '/api/auth/setup/status') return json({ needsSetup: false });
      if (path === '/api/auth/refresh') return json({ accessToken: 'synthetic-browser-token', tokenType: 'Bearer', expiresInSeconds: 3600 });
      if (path === '/api/auth/me') return json({ id: 'owner', role: 'admin', username: 'owner', isActive: true });
      if (path === '/api/settings/providers') return json({ providers: [{ id: 'synthetic', type: 'openai' }] });
      if (path === '/api/runtime/execution-readiness') return json({ ready: true, code: 'READY', message: 'Ready', dependencies: [] });
      if (path === '/api/network-access/requests/request-1/decision') {
        decisions.push(request.postDataJSON());
        state.revision = 8; state.activeRevision = 8; state.requests[0]!.status = 'allowed';
        return json(state);
      }
      if (path === '/api/network-access') return json(state);
      return json({});
    });
    await page.goto('/network-access');
    await expect(page.getByRole('heading', { name: 'Network access', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Keep blocked', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Allow', exact: true }).click();
    await expect(page.getByText('No pending network approvals.')).toBeVisible();
    expect(decisions).toEqual([{ allowed: true, expectedRevision: 7 }]);
    await expect(page.getByLabel('Hostname or IPv4 address')).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await page.screenshot({ path: `artifacts/network-access-${width}.png`, fullPage: true });
  });
}

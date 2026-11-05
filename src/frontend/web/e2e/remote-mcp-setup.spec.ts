import { expect, test } from '@playwright/test';

for (const viewport of [
  { width: 1280, height: 900 },
  { width: 390, height: 844 },
]) {
  test(`remote setup keeps keys on credential administration at ${viewport.width}px`, async ({
    page,
  }) => {
    await page.setViewportSize(viewport);
    const requests: { path: string; body: string | null }[] = [];
    await page.route('**/api/**', async (route) => {
      const request = route.request();
      const path = new URL(request.url()).pathname;
      const json = (body: unknown) => route.fulfill({ json: body });
      if (request.method() !== 'GET') requests.push({ path, body: request.postData() });
      if (path === '/api/auth/setup/status') return json({ needsSetup: false });
      if (path === '/api/auth/refresh')
        return json({
          accessToken: 'synthetic-browser-token',
          tokenType: 'Bearer',
          expiresInSeconds: 3600,
        });
      if (path === '/api/auth/me')
        return json({
          id: 'owner',
          role: 'admin',
          username: 'owner',
          isActive: true,
          canImportConnectors: true,
        });
      if (path === '/api/settings/providers')
        return json({ providers: [{ id: 'synthetic', type: 'openai' }] });
      if (path === '/api/runtime/execution-readiness')
        return json({ ready: true, code: 'READY', message: 'Ready', dependencies: [] });
      if (path === '/api/connectors') return json({ connectors: [] });
      if (path === '/api/connectors/oauth') return json({ connectors: [] });
      if (path === '/api/credentials/connector-drafts')
        return request.method() === 'POST'
          ? json({ draftId: 'opaque-browser-draft', expiresAt: '2026-11-05T23:00:00Z' })
          : json({ drafts: [] });
      if (path === '/api/connectors/drafts/opaque-browser-draft/test')
        return json({ success: true, message: 'Connected.', toolCount: 1, toolNames: ['search'] });
      if (path === '/api/connectors/drafts/opaque-browser-draft/save')
        return json({
          id: 'search-example',
          name: 'Search',
          transport: 'http',
          httpUrl: 'https://search.example/mcp',
        });
      return json({});
    });
    await page.goto('/connectors');
    await page.getByRole('button', { name: 'Add remote MCP', exact: true }).first().click();
    await page.getByLabel('Server URL', { exact: true }).fill('https://search.example/mcp');
    await page.getByLabel('Name', { exact: true }).fill('Search');
    await page
      .getByRole('combobox', { name: 'Authentication', exact: true })
      .selectOption('bearer');
    const key = page.getByLabel('API key (required)', { exact: true });
    await key.fill('synthetic-browser-only-key');
    await expect(key).toHaveAttribute('type', 'password');
    await page.getByRole('button', { name: 'Test connection', exact: true }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Connected.' })).toBeVisible();
    await expect(page.getByRole('dialog')).toBeVisible();
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)
    ).toBe(true);
    await page.screenshot({ path: `artifacts/remote-mcp-${viewport.width}.png`, fullPage: true });
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    const secretRequests = requests.filter((request) =>
      request.body?.includes('synthetic-browser-only-key')
    );
    expect(secretRequests.map((request) => request.path)).toEqual([
      '/api/credentials/connector-drafts',
    ]);
    expect(requests.find((request) => request.path.endsWith('/test'))?.body).toBeNull();
    expect(requests.find((request) => request.path.endsWith('/save'))?.body).toBeNull();
    const draft = JSON.parse(secretRequests[0]!.body!) as { candidate: unknown };
    expect(JSON.stringify(draft.candidate)).not.toContain('synthetic-browser-only-key');
  });
}

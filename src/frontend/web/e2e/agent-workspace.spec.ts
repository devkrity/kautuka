import { createServer, request as httpRequest, type ServerResponse } from 'node:http';
import type { AddressInfo } from 'node:net';
import { expect, test as base } from '@playwright/test';

const timestamp = '2026-10-24T00:00:00Z';
const agents = [
  { id: 'alpha', name: 'Alpha', scope: 'system', ownerUserId: null },
  { id: 'beta', name: 'Beta', scope: 'system', ownerUserId: null },
];
const session = (id: string, agentId: string, title: string) => ({
  id, targetType: 'agent', targetId: agentId, title, createdAt: timestamp, updatedAt: timestamp,
  messages: [], compacted: false,
});
const sessions = {
  alpha: [session('alpha-1', 'alpha', 'Alpha first'), session('alpha-2', 'alpha', 'Alpha second')],
  beta: [session('beta-1', 'beta', 'Beta first'), session('beta-2', 'beta', 'Beta second')],
};

type WorkspaceFixture = {
  origin: string;
  requests: string[];
  releaseHistory: () => void;
  holdHistory: () => void;
};

const test = base.extend<{ workspace: WorkspaceFixture }>({
  workspace: async ({}, use) => {
    const requests: string[] = [];
    const streams = new Set<ServerResponse>();
    let historyGate: Promise<void> | null = null;
    let release: (() => void) | null = null;
    const server = createServer((req, res) => {
      const url = new URL(req.url ?? '/', 'http://localhost');
      if (!url.pathname.startsWith('/api/')) {
        const upstream = httpRequest(`http://127.0.0.1:4173${req.url ?? '/'}`, { method: req.method, headers: req.headers }, (response) => {
          res.writeHead(response.statusCode ?? 500, response.headers);
          response.pipe(res);
        });
        upstream.on('error', () => { if (!res.headersSent) res.writeHead(502); res.end(); });
        req.pipe(upstream);
        return;
      }
      requests.push(`${req.method} ${url.pathname}${url.search}`);
      const json = (value: unknown, status = 200) => {
        res.writeHead(status, { 'content-type': 'application/json', 'cache-control': 'no-store' });
        res.end(JSON.stringify(value));
      };
      const path = url.pathname;
      if (path === '/api/auth/setup/status') return json({ needsSetup: false });
      if (path === '/api/auth/refresh') return json({ accessToken: 'e2e-token', tokenType: 'Bearer', expiresInSeconds: 3600 });
      if (path === '/api/auth/me') return json({ id: 'owner', role: 'Admin', userName: 'Owner' });
      if (path === '/api/settings/agents') return json(agents);
      if (path.startsWith('/api/sessions/agents/')) return json(sessions[path.split('/').at(-1) as keyof typeof sessions] ?? []);
      const sessionId = path.match(/^\/api\/narad\/v1\/sessions\/([^/]+)\/(?:events|stream)$/)?.[1]
        ?? (path === '/api/narad/v1/capabilities' ? url.searchParams.get('sessionId') : null);
      const agentId = url.searchParams.get('agentId');
      if (sessionId && agentId && !(sessions[agentId as keyof typeof sessions] ?? []).some((item) => item.id === sessionId)) {
        return json({ error: 'Session does not belong to the agent.' }, 403);
      }
      if (path === '/api/narad/v1/capabilities') return json({ protocolVersion: 'narad/v1', activeProfiles: ['narad.conversation/v1', 'narad.graph/v1', 'narad.progress/v1', 'narad.ui/v1'], currentSessionSequence: 3 });
      if (/^\/api\/narad\/v1\/sessions\/[^/]+\/events$/.test(path)) {
        const id = path.split('/')[5]!;
        const after = Number(url.searchParams.get('afterSessionSequence') ?? 0);
        const events = [
          { type: 'run.started', protocolVersion: 'narad/v1', eventId: `${id}-start`, timestamp, sessionId: id, sessionSequence: 1, rootRunId: `${id}-run`, runId: `${id}-run`, sequence: 1 },
          { type: 'message.user.recorded', protocolVersion: 'narad/v1', eventId: `${id}-user`, timestamp, sessionId: id, sessionSequence: 2, messageId: `${id}-message`, content: `Previous request ${id}`, messageScope: 'session', rootRunId: `${id}-run` },
          { type: 'run.finished', protocolVersion: 'narad/v1', eventId: `${id}-finish`, timestamp, sessionId: id, sessionSequence: 3, rootRunId: `${id}-run`, runId: `${id}-run`, sequence: 2 },
        ].filter((event) => event.sessionSequence > after);
        void (historyGate ?? Promise.resolve()).then(() => { if (!res.destroyed) json({ events, nextSessionSequence: null }); });
        return;
      }
      if (/^\/api\/narad\/v1\/sessions\/[^/]+\/stream$/.test(path)) {
        res.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache', connection: 'keep-alive' });
        res.write(': connected\n\n');
        streams.add(res);
        res.on('close', () => streams.delete(res));
        return;
      }
      if (path === '/api/runtime/execution-readiness') return json({ ready: true, code: 'READY', message: 'Ready', dependencies: [] });
      if (path.endsWith('/assets')) return json({ assets: [] });
      if (path.includes('/queue')) return json({ messages: [] });
      if (path.includes('/approvals')) return json([]);
      return json({});
    });
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
    const origin = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
    try {
      await use({
        origin, requests,
        holdHistory: () => { historyGate = new Promise<void>((resolve) => { release = resolve; }); },
        releaseHistory: () => { release?.(); historyGate = null; release = null; },
      });
    } finally {
      release?.();
      for (const stream of streams) stream.destroy();
      server.closeAllConnections();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }
  },
});

test('agent switches and cross-agent deep links keep the correct session', async ({ page, workspace }) => {
  await page.goto(`${workspace.origin}/workspace/alpha?sessionId=alpha-1`);
  await expect(page.getByText('Ready', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: /Alpha second/ }).click();
  await expect(page).toHaveURL(/sessionId=alpha-2/);
  await expect(page.getByRole('button', { name: /Alpha second/ })).toHaveAttribute('aria-current', 'page');
  await page.getByRole('button', { name: 'Beta', exact: true }).click();
  await expect(page).toHaveURL(/\/workspace\/beta\?sessionId=beta-1/);
  await expect(page.getByText('Session access denied.')).toHaveCount(0);
  expect(workspace.requests.some((request) => /alpha-[12]/.test(request) && request.includes('agentId=beta'))).toBe(false);
  await page.goto(`${workspace.origin}/workspace/beta?sessionId=beta-2`);
  await expect(page.getByRole('button', { name: /Beta second/ })).toHaveAttribute('aria-current', 'page');
  await expect(page).toHaveURL(/sessionId=beta-2/);
});

test('historical runs are not shown as working while history is loading', async ({ page, workspace }) => {
  workspace.holdHistory();
  await page.goto(`${workspace.origin}/workspace/alpha?sessionId=alpha-1`);
  await expect(page.getByText('Loading history')).toBeVisible();
  await expect(page.getByText('Working', { exact: true })).toHaveCount(0);
  await expect(page.getByText('Previous request alpha-1')).toHaveCount(0);
  workspace.releaseHistory();
  await expect(page.getByText('Ready', { exact: true })).toBeVisible();
  await expect(page.getByText('Previous request alpha-1')).toBeVisible();
});

test('three workspace tabs stay responsive with live streams open', async ({ context, workspace }) => {
  const pages = await Promise.all([context.newPage(), context.newPage(), context.newPage()]);
  try {
    await Promise.all(pages.map((page) => page.goto(`${workspace.origin}/workspace/alpha?sessionId=alpha-1`)));
    await Promise.all(pages.map((page) => expect(page.getByText('Ready', { exact: true })).toBeVisible()));
    await pages[1]!.getByRole('link', { name: 'Approvals' }).click();
    await expect(pages[1]!.getByRole('heading', { name: 'Approvals' })).toBeVisible();
    await pages[2]!.reload();
    await expect(pages[2]!.getByText('Ready', { exact: true })).toBeVisible();
    expect(workspace.requests.some((request) => request.includes('/approvals/changes'))).toBe(false);
    expect(workspace.requests.filter((request) => request.includes('/stream?') && request.includes('includeWatch=true')).length).toBeGreaterThanOrEqual(3);
  } finally {
    await Promise.all(pages.map((page) => page.close()));
  }
});

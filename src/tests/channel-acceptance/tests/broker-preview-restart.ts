import { expect, type APIRequestContext } from '@playwright/test';
import { resolve } from 'node:path';
import type { OwnedStackLifecycle } from '../src/lifecycle.js';

export async function restartPreviewMail(lifecycle: OwnedStackLifecycle, request: APIRequestContext, base: URL,
  stateRoot: string, password: string): Promise<{ token: string; watermark: number }> {
  await lifecycle.restartOwnedProcess('Gateway');
  let watermark = 0;
  if (process.env.KAUTUKA_TEST_MAIL_OPERATIONS === '1') {
    const { DatabaseSync } = await import('node:sqlite');
    const db = new DatabaseSync(resolve(stateRoot, 'agents', 'preview-mail', 'agent.db'), { readOnly: true });
    try { watermark = Number((db.prepare('SELECT COALESCE(MAX(rowid), 0) AS id FROM narad_runtime_journal').get() as { id: number }).id); }
    finally { db.close(); }
  }
  const login = await request.post(new URL('/api/auth/login', base).toString(), { data: { username: 'preview-admin', password } });
  expect(login.ok()).toBe(true);
  const token = (await login.json()).accessToken as string;
  const catalog = await request.get(new URL('/api/connectors/oauth', base).toString(), { headers: { authorization: `Bearer ${token}` } });
  expect(catalog.ok()).toBe(true);
  const statuses = await catalog.json() as { connectors: { connectorId: string; connected: boolean }[] };
  expect(['kautuka-google-mail', 'kautuka-m365-mail'].every(id =>
    statuses.connectors.some(item => item.connectorId === id && item.connected))).toBe(true);
  console.log('Both mailbox connections survived the isolated Gateway restart.');
  return { token, watermark };
}

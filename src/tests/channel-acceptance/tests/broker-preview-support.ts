import { expect, type APIRequestContext } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';

export async function assertPreviewFrontend(request: APIRequestContext, base: URL): Promise<void> {
  const expected = await readFile(resolve(process.cwd(), '../../..', 'src/frontend/web/dist/index.html'), 'utf8');
  const response = await request.get(base.toString());
  expect(response.ok()).toBe(true);
  expect(await response.text()).toBe(expected);
}

export async function waitForPreviewMail(request: APIRequestContext, base: URL, token: string): Promise<void> {
  await expect.poll(async () => {
    const response = await request.get(new URL('/api/connectors/oauth', base).toString(), { headers: { authorization: `Bearer ${token}` } });
    if (!response.ok()) return false;
    const catalog = await response.json() as { connectors: { connectorId: string; connected: boolean }[] };
    return ['kautuka-google-mail', 'kautuka-m365-mail'].every(id => catalog.connectors.some(item => item.connectorId === id && item.connected));
  }, { timeout: 600_000, intervals: [2000] }).toBe(true);
}

export async function configurePreviewAgent(request: APIRequestContext, base: URL, accessToken: string): Promise<void> {
        const headers = { authorization: `Bearer ${accessToken}` };
        const save = async (path: string, data: unknown, method: 'post' | 'put' = 'post') => {
          const result = await request[method](new URL(path, base).toString(), { headers, data });
          expect(result.status(), `configure ${path}`).toBeLessThan(300);
        };
        await save('/api/settings/providers/save', { id: 'preview-ollama', type: 'ollama', settings: { base_url: 'http://127.0.0.1:11434' } });
        await save('/api/settings/models/save', { id: 'preview-model', providerId: 'preview-ollama', modelName: 'mistral-small:24b', settings: {} });
        await save('/api/settings/agents/preview-mail', {
          name: 'Preview Mail Test', modelId: 'preview-model', systemPrompt: 'Use the requested mail connector to read the inbox. Do not send or modify email.',
          mcpBindings: [{ connectorId: 'kautuka-google-mail', context: 'OwnContext' }, { connectorId: 'kautuka-m365-mail', context: 'OwnContext' }],
          skillAttachments: [], session: { historyEnabled: true, maxHistoryRuns: 5, summariesEnabled: false },
          sandbox: { enabled: false, allowedLanguages: [], maxMemory: '2g', maxTimeout: 120 },
          approvalConfig: { requireMcpIds: [], requireMcpFunctionFilters: {}, disabledMcpFunctions: {
            'kautuka-google-mail': ['gmail_create_draft', 'gmail_update_draft', 'gmail_send'],
            'kautuka-m365-mail': ['mail_create_draft', 'mail_update_draft', 'mail_send'],
          }, timeoutSeconds: 300 },
        }, 'put');
}

export async function writePreviewPassword(stateRoot: string, password: string): Promise<void> {
      const privateDirectory = resolve(stateRoot, 'test-private');
      await mkdir(privateDirectory, { mode: 0o700 });
      if (process.platform === 'win32') {
        const identity = execFileSync('whoami.exe', ['/user', '/fo', 'csv', '/nh'], { encoding: 'utf8' });
        const sid = identity.match(/S-1-(?:\d+-)+\d+/)?.[0];
        if (!sid) throw new Error('Cannot identify the Windows test account for password protection.');
        execFileSync('icacls.exe', [privateDirectory, '/inheritance:r', '/grant:r', `*${sid}:(OI)(CI)F`], { stdio: 'ignore' });
      }
      const passwordFile = resolve(privateDirectory, 'preview-admin-password');
      await writeFile(passwordFile, password, { mode: 0o600 });
      console.log(`Isolated preview-admin password file: ${passwordFile}`);
}

export async function assertPreviewMailOperations(stateRoot: string, journalWatermark: number): Promise<void> {
        // Inspect only tool names, call IDs and outcomes, never mailbox content.
        const { DatabaseSync } = await import('node:sqlite');
        const db = new DatabaseSync(resolve(stateRoot, 'agents', 'preview-mail', 'agent.db'), { readOnly: true });
        try {
          const events = db.prepare("SELECT event_type, event_json FROM narad_runtime_journal WHERE rowid > ? AND event_type IN ('tool.call.started', 'tool.call.finished')")
            .all(journalWatermark) as { event_type: string; event_json: string }[];
          const started = new Map<string, string>();
          const succeeded = new Set<string>();
          for (const row of events) {
            const event = JSON.parse(row.event_json) as { toolCallId: string; toolName?: string; outcome?: string };
            if (row.event_type === 'tool.call.started' && event.toolName) started.set(event.toolCallId, event.toolName);
          }
          for (const row of events) {
            const event = JSON.parse(row.event_json) as { toolCallId: string; outcome?: string };
            const name = started.get(event.toolCallId);
            if (row.event_type === 'tool.call.finished' && event.outcome === 'success' && name) succeeded.add(name);
          }
          expect(succeeded.has('mail_list'), 'Microsoft mail read').toBe(true);
          expect(succeeded.has('gmail_list'), 'Gmail read').toBe(true);
        } finally { db.close(); }
}

export async function cleanupPreviewMail(request: APIRequestContext, base: URL, password: string): Promise<boolean> {
  let failed = false;
  try {
    const login = await request.post(new URL('/api/auth/login', base).toString(), { data: { username: 'preview-admin', password } });
    if (!login.ok()) throw new Error(`relogin HTTP ${login.status()}`);
    const token = (await login.json()).accessToken as string;
    for (const id of ['kautuka-google-mail', 'kautuka-m365-mail']) {
      const response = await request.delete(new URL(`/api/connectors/${id}/oauth`, base).toString(), {
        headers: { authorization: `Bearer ${token}` },
      });
      if (!response.ok() || !(await response.json()).disconnected) {
        failed = true;
        console.warn(`Preview cleanup did not disconnect ${id}.`);
      }
    }
  } catch { failed = true; console.warn('Preview cleanup could not authenticate or reach the Gateway.'); }
  return failed;
}

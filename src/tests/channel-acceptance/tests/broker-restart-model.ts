import { expect, type APIRequestContext } from '@playwright/test';
import { createServer, type Server } from 'node:http';

export async function startRestartModel(request: APIRequestContext, base: URL, headers: Record<string, string>):
Promise<{ server: Server; reset: () => void }> {
  let calls = 0;
  const server = createServer((incoming, response) => {
    if (incoming.url !== '/api/chat' || incoming.method !== 'POST') { response.writeHead(404).end(); return; }
    let body = '';
    incoming.setEncoding('utf8');
    incoming.on('data', chunk => { body += chunk; });
    incoming.on('end', () => {
      response.writeHead(200, { 'content-type': 'application/x-ndjson' });
      const payload = JSON.parse(body) as { tools?: { function?: { name?: string } }[] };
      const hasMailTool = payload.tools?.some(tool => tool.function?.name === 'gmail_list') === true;
      const message = hasMailTool && calls++ === 0
        ? { role: 'assistant', content: '', tool_calls: [{ function: { name: 'gmail_list', arguments: {} } }] }
        : { role: 'assistant', content: 'Test complete.' };
      response.end(`${JSON.stringify({ model: 'local-test', created_at: new Date().toISOString(), message,
        done: true, done_reason: 'stop' })}\n`);
    });
  });
  await new Promise<void>((done, reject) => server.listen(0, '127.0.0.1', done).once('error', reject));
  try {
    const modelPort = (server.address() as { port: number }).port;
    const save = async (path: string, data: unknown, method: 'post' | 'put' = 'post') => {
      const result = await request[method](new URL(path, base).toString(), { headers, data });
      expect(result.ok(), path).toBe(true);
    };
    await save('/api/settings/providers/save', { id: 'restart-ollama', type: 'ollama', settings: { base_url: `http://127.0.0.1:${modelPort}` } });
    await save('/api/settings/models/save', { id: 'restart-model', providerId: 'restart-ollama', modelName: 'local-test', settings: {} });
    await save('/api/settings/agents/restart-agent', { name: 'Restart test', modelId: 'restart-model', systemPrompt: 'Read only.',
      mcpBindings: [{ connectorId: 'kautuka-google-mail', context: 'OwnContext' }], skillAttachments: [],
      session: { historyEnabled: true, maxHistoryRuns: 5, summariesEnabled: false },
      sandbox: { enabled: false, allowedLanguages: [], maxMemory: '2g', maxTimeout: 120 },
      approvalConfig: { requireMcpIds: [], requireMcpFunctionFilters: {}, disabledMcpFunctions: {
        'kautuka-google-mail': ['gmail_create_draft', 'gmail_update_draft', 'gmail_send'],
      }, timeoutSeconds: 300 } }, 'put');
    await expect.poll(async () => {
      const readiness = await request.get(new URL('/api/runtime/execution-readiness', base).toString(), { headers });
      return readiness.ok() && (await readiness.json()).ready === true;
    }, { timeout: 90_000, intervals: [1000] }).toBe(true);
    await expect.poll(async () => {
      const connector = await request.get(new URL('/api/connectors/kautuka-google-mail', base).toString(), { headers });
      return connector.ok() && typeof (await connector.json()).bundlePath === 'string';
    }, { timeout: 90_000, intervals: [1000] }).toBe(true);
    return { server, reset: () => { calls = 0; } };
  } catch (error) {
    await new Promise<void>(done => server.close(() => done()));
    throw error;
  }
}

export async function sendRestartMailPrompt(request: APIRequestContext, base: URL, headers: Record<string, string>): Promise<void> {
  const created = await request.post(new URL('/api/sessions', base).toString(), { headers, data: { targetType: 'agent', targetId: 'restart-agent' } });
  expect(created.ok()).toBe(true);
  const id = (await created.json()).id as string;
  const message = await request.post(new URL(`/api/narad/v1/agents/restart-agent/sessions/${id}/messages`, base).toString(), {
    headers, data: { message: 'List the inbox.', expectedRevision: 0 },
  });
  expect(message.ok()).toBe(true);
}

import type { Page } from '@playwright/test';

export async function installNaradFetchCaptureHook(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const originalFetch = globalThis.fetch;
    globalThis.fetch = async (...args) => {
      const response = await originalFetch(...args);
      const address = typeof args[0] === 'string' ? args[0] : args[0] instanceof Request ? args[0].url : '';
      const pathname = new URL(address, globalThis.location.origin).pathname;
      if (!pathname.startsWith('/api/narad/v1/')) return response;
      const endpointKind = (() => {
        if (/\/sessions\/[^/]+\/stream$/.test(pathname)) return 'stream';
        if (/\/sessions\/[^/]+\/watch$/.test(pathname)) return 'watch';
        if (/\/workspace\/watch$/.test(pathname)) return 'workspace-watch';
        if (/\/sessions\/[^/]+\/events$/.test(pathname)) return 'events';
        return undefined;
      })();
      const reader = response.clone().body?.getReader();
      if (!reader) return response;
      void (async () => {
        let buffer = '';
        const decoder = new TextDecoder();
        while (true) {
          const next = await reader.read();
          if (next.done) return;
          buffer += decoder.decode(next.value, { stream: true });
          const frames = buffer.split(/\r?\n\r?\n/);
          buffer = frames.pop() ?? '';
          for (const frame of frames) {
            const data = frame.split(/\r?\n/).filter((line) => line.startsWith('data:')).map((line) => line.slice(5).trim()).join('');
            if (!data) continue;
            try {
              const payload = JSON.parse(data) as Record<string, unknown>;
              if (typeof payload.type === 'string') {
                const sanitized: Record<string, unknown> = { type: payload.type };
                for (const field of ['protocolVersion', 'eventId', 'sessionId', 'sessionSequence', 'rootRunId', 'runId', 'sequence', 'privacy', 'progressId', 'revision', 'workspaceId', 'toolCallId', 'toolName', 'kind'] as const) {
                  if (field in payload) sanitized[field] = payload[field];
                }
                if (endpointKind) sanitized.endpointKind = endpointKind;
                await (globalThis as typeof globalThis & { __kautukaNaradRecord: (value: unknown) => Promise<void> }).__kautukaNaradRecord(sanitized);
              }
            } catch { /* ignore malformed frames */ }
          }
        }
      })().catch(() => undefined);
      return response;
    };
  });
}

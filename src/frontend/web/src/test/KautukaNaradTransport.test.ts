import { describe, expect, it, vi } from 'vitest';
import {
  createKautukaNaradTransport,
  fetchKautukaNaradHistoryPages,
  type AuthenticatedFetch,
} from '../features/chat/kautuka/createKautukaNaradTransport';
import { NaradClientError, createSseFrameParser, parseSseFrameData } from '@devkrity/narad';

function mockResponse(body: unknown, init: ResponseInit = {}): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
    ...init,
  });
}

function sseResponse(chunks: string[], init: ResponseInit = {}): Response {
  const encoder = new TextEncoder();
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      for (const chunk of chunks) {
        controller.enqueue(encoder.encode(chunk));
      }
      controller.close();
    },
  });
  return new Response(stream, {
    status: 200,
    headers: { 'Content-Type': 'text/event-stream' },
    ...init,
  });
}

describe('createKautukaNaradTransport', () => {
  it('uses authenticated Narad request shapes for capabilities and history', async () => {
    const fetchMock = vi.fn<AuthenticatedFetch>(async (input, init) => {
      const url = String(input);
      if (url.includes('/capabilities?')) {
        expect(url).toContain('sessionId=session-1');
        expect(url).toContain('agentId=agent-1');
        expect(init?.credentials).toBe('include');
        return mockResponse({
          protocolVersion: 'narad/v1',
          activeProfiles: ['narad.conversation/v1'],
          currentSessionSequence: 2,
          heartbeatIntervalMs: 15000,
        });
      }
      if (url.includes('/events?')) {
        expect(url).toContain('/sessions/session-1/events?');
        expect(url).toContain('afterSessionSequence=2');
        expect(url).toContain('limit=100');
        return mockResponse({
          events: [{ type: 'run.started', eventId: 'evt-2', sessionSequence: 3 }],
          nextSessionSequence: null,
        });
      }
      throw new Error(`Unexpected URL ${url}`);
    });

    const transport = createKautukaNaradTransport({ agentId: 'agent-1', fetch: fetchMock });
    const attach = await transport.attach('session-1');
    expect(attach.boundarySessionSequence).toBe(2);
    const page = await transport.fetchHistory('session-1', 2, 100);
    expect(page.events).toHaveLength(1);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    transport.dispose();
  });

  it('paginates catch-up history through transport pages', async () => {
    const fetchMock = vi.fn<AuthenticatedFetch>(async (input) => {
      const url = String(input);
      if (url.includes('afterSessionSequence=0')) {
        return mockResponse({
          events: [{ type: 'run.started', eventId: 'evt-1', sessionSequence: 1 }],
          nextSessionSequence: 1,
        });
      }
      if (url.includes('afterSessionSequence=1')) {
        return mockResponse({
          events: [{ type: 'run.finished', eventId: 'evt-2', sessionSequence: 2 }],
          nextSessionSequence: null,
        });
      }
      throw new Error(`Unexpected URL ${url}`);
    });

    const transport = createKautukaNaradTransport({ agentId: 'agent-1', fetch: fetchMock });
    const applied: unknown[] = [];
    await fetchKautukaNaradHistoryPages(transport, 'session-1', 0, 1, (record) => applied.push(record));
    expect(applied).toHaveLength(2);
    transport.dispose();
  });

  it('parses fragmented SSE frames for stream delivery', async () => {
    const parser = createSseFrameParser();
    const part1 = parser.push('id: evt-1\nevent: message\n');
    expect(part1).toEqual([]);
    const part2 = parser.push(
      'data: {"type":"run.started","protocolVersion":"narad/v1","eventId":"evt-1","timestamp":"2026-08-05T00:00:00Z","sessionId":"session-1","sessionSequence":1,"rootRunId":"run-1","runId":"run-1","sequence":1}\n\n',
    );
    expect(part2).toHaveLength(1);
    expect(parseSseFrameData(part2[0]!)).toMatchObject({ type: 'run.started', eventId: 'evt-1' });
  });

  it('reconnects live streams from the last delivered cursor', async () => {
    const streamUrls: string[] = [];
    const fetchMock = vi.fn<AuthenticatedFetch>(async (input) => {
      const url = String(input);
      if (url.includes('/stream?')) {
        streamUrls.push(url);
        if (streamUrls.length === 1) {
          return sseResponse([
            'id: evt-1\nevent: message\ndata: {"type":"run.started","protocolVersion":"narad/v1","eventId":"evt-1","timestamp":"2026-08-05T00:00:00Z","sessionId":"session-1","sessionSequence":1,"rootRunId":"run-1","runId":"run-1","sequence":1}\n\n',
          ]);
        }
        return sseResponse([
          'data: {"type":"graph.snapshot","protocolVersion":"narad/v1","timestamp":"2026-08-05T00:00:01Z","sessionId":"session-1","scope":"run","rootRunId":"run-1","graphSchemaVersion":"narad.graph/v1","revision":1,"sourceRunCursor":{"rootRunId":"run-1","sequence":1},"rootNodeId":"root","nodes":[{"id":"root","kind":"run","status":"running","label":"Root"}],"edges":[]}\n\n',
          'id: evt-2\nevent: message\ndata: {"type":"run.finished","protocolVersion":"narad/v1","eventId":"evt-2","timestamp":"2026-08-05T00:00:01Z","sessionId":"session-1","sessionSequence":2,"rootRunId":"run-1","runId":"run-1","sequence":2}\n\n',
        ]);
      }
      throw new Error(`Unexpected URL ${url}`);
    });

    const transport = createKautukaNaradTransport({ agentId: 'agent-1', fetch: fetchMock });
    const abort = new AbortController();
    const iterator = transport.subscribeLive('session-1', 0, abort.signal)[Symbol.asyncIterator]();
    const first = await iterator.next();
    const second = await iterator.next();
    const third = await iterator.next();
    abort.abort();
    transport.dispose();
    expect(first.value).toMatchObject({ sessionSequence: 1 });
    expect(second.value).toMatchObject({ type: 'graph.snapshot' });
    expect(third.value).toMatchObject({ sessionSequence: 2 });
    expect(streamUrls[1]).toContain('afterSessionSequence=1');
  });

  it('preserves authority and revision order within one stream read', async () => {
    const fetchMock = vi.fn<AuthenticatedFetch>(async (input) => {
      expect(String(input)).toContain('includeWatch=true');
      return sseResponse([
        'id: evt-1\nevent: message\ndata: {"type":"run.started","protocolVersion":"narad/v1","eventId":"evt-1","timestamp":"2026-08-05T00:00:00Z","sessionId":"session-1","sessionSequence":1,"rootRunId":"run-1","runId":"run-1","sequence":1}\n\nid: session-revision:session-1:1\nevent: watch\ndata: {"type":"session.revision","protocolVersion":"narad/v1","eventId":"session-revision:session-1:1","timestamp":"2026-08-05T00:00:00Z","sessionId":"session-1","revision":1,"cursor":{"kind":"session","sessionSequence":1}}\n\n',
      ]);
    });
    const transport = createKautukaNaradTransport({ agentId: 'agent-1', fetch: fetchMock });
    const abort = new AbortController();
    const iterator = transport.subscribeLive('session-1', 0, abort.signal)[Symbol.asyncIterator]();
    const first = await iterator.next();
    const second = await iterator.next();
    abort.abort();
    transport.dispose();
    expect(first.value).toMatchObject({ type: 'run.started' });
    expect(second.value).toMatchObject({ type: 'session.revision' });
  });

  it('fails closed on unauthorized responses', async () => {
    const fetchMock = vi.fn<AuthenticatedFetch>(async () =>
      mockResponse({ detail: 'Forbidden.' }, { status: 403 }),
    );
    const transport = createKautukaNaradTransport({ agentId: 'agent-1', fetch: fetchMock });
    await expect(transport.attach('session-1')).rejects.toBeInstanceOf(NaradClientError);
    transport.dispose();
  });

  it('rejects malformed authority SSE frames without ids', async () => {
    const fetchMock = vi.fn<AuthenticatedFetch>(async (input) => {
      if (String(input).includes('/stream?')) {
        return sseResponse(['data: {"type":"run.started","eventId":"evt-1"}\n\n']);
      }
      return mockResponse({ protocolVersion: 'narad/v1', activeProfiles: [], currentSessionSequence: 0 });
    });
    const transport = createKautukaNaradTransport({ agentId: 'agent-1', fetch: fetchMock });
    const abort = new AbortController();
    const iterator = transport.subscribeLive('session-1', 0, abort.signal)[Symbol.asyncIterator]();
    await expect(iterator.next()).rejects.toBeInstanceOf(NaradClientError);
    abort.abort();
    transport.dispose();
  });

  it('aborts in-flight fetch on dispose', async () => {
    let aborted = false;
    const fetchMock = vi.fn<AuthenticatedFetch>(async (_input, init) => {
      init?.signal?.addEventListener('abort', () => {
        aborted = true;
      });
      await new Promise((resolve) => setTimeout(resolve, 25));
      return mockResponse({ protocolVersion: 'narad/v1', activeProfiles: [], currentSessionSequence: 0 });
    });
    const transport = createKautukaNaradTransport({ agentId: 'agent-1', fetch: fetchMock });
    const pending = transport.attach('session-1');
    transport.dispose();
    await pending.catch(() => undefined);
    expect(aborted).toBe(true);
  });
});

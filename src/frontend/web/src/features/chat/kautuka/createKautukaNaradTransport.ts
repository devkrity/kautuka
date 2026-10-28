import {
  NaradClientError,
  classifyWireRecord,
  createSseFrameParser,
  logNaradError,
  logNaradSse,
  parseSseFrameData,
  redactForPublicError,
  validateSseAuthorityFrameId,
  type NaradLimits,
  type NaradNegotiatedCapabilities,
  type NaradProfileId,
  type NaradTransport,
  type NaradTransportAttachResult,
  type SessionHistoryPage,
  DEFAULT_NARAD_LIMITS,
} from '@devkrity/narad';

const NARAD_BASE = '/api/narad/v1';

export type AuthenticatedFetch = (
  input: RequestInfo | URL,
  init?: RequestInit,
) => Promise<Response>;

export interface KautukaNaradTransportOptions {
  readonly fetch: AuthenticatedFetch;
  readonly claimedProfiles?: readonly NaradProfileId[];
  readonly historyPageSize?: number;
  readonly limits?: NaradLimits;
  readonly agentId?: string;
  readonly workflowId?: string;
  readonly instanceId?: string;
}

function workflowBase(options: KautukaNaradTransportOptions): string | null {
  if (!options.workflowId || !options.instanceId) {
    return null;
  }
  return `${NARAD_BASE}/workflows/${encodeURIComponent(options.workflowId)}/instances/${encodeURIComponent(options.instanceId)}`;
}

function readJsonRecord(response: Response): Promise<Record<string, unknown>> {
  return response.json().catch(() => ({})) as Promise<Record<string, unknown>>;
}

function parseCapabilities(payload: Record<string, unknown>): NaradNegotiatedCapabilities {
  const activeProfiles = Array.isArray(payload.activeProfiles)
    ? payload.activeProfiles.filter((profile): profile is NaradProfileId => typeof profile === 'string')
    : [];
  const currentSessionSequence =
    typeof payload.currentSessionSequence === 'number' ? payload.currentSessionSequence : 0;
  return {
    protocolVersion: 'narad/v1',
    activeProfiles,
    currentSessionSequence,
    historyLimit: typeof payload.historyLimit === 'number' ? payload.historyLimit : undefined,
    heartbeatIntervalMs:
      typeof payload.heartbeatIntervalMs === 'number' ? payload.heartbeatIntervalMs : undefined,
    activeRootRunId:
      typeof payload.activeRootRunId === 'string' ? payload.activeRootRunId : undefined,
    activeWorkflowId:
      typeof payload.activeWorkflowId === 'string' ? payload.activeWorkflowId : undefined,
  };
}

function shouldYieldToReducer(record: unknown): boolean {
  const wireClass = classifyWireRecord(record);
  return wireClass === 'authority' || wireClass === 'hydrate' || wireClass === 'watch';
}

function failClosedStatus(status: number): never {
  if (status === 401 || status === 403) {
    logNaradError('transport', 'unauthorized', `HTTP ${status}`, { status });
    throw new NaradClientError('unauthorized', 'Session access denied.');
  }
  logNaradError('transport', 'request failed', `HTTP ${status}`, { status });
  throw new NaradClientError('transport_failed', `Narad request failed with HTTP ${status}.`);
}

async function consumeSseResponse(
  response: Response,
  signal: AbortSignal,
  limits: NaradLimits,
  onRecord: (record: unknown) => void,
): Promise<void> {
  if (!response.ok) {
    failClosedStatus(response.status);
  }
  if (response.body === null) {
    throw new NaradClientError('transport_failed', 'Narad stream returned an empty body.');
  }

  const parser = createSseFrameParser({ limits });
  const reader = response.body.getReader();
  const decoder = new TextDecoder();

  while (!signal.aborted) {
    const { done, value } = await reader.read();
    if (done) {
      return;
    }
    const frames = parser.push(decoder.decode(value, { stream: true }));
    for (const frame of frames) {
      if (signal.aborted) {
        return;
      }
      let record: unknown;
      try {
        record = parseSseFrameData(frame);
      } catch (error) {
        logNaradError('sse', 'invalid SSE frame', error, { frameId: frame.id, event: frame.event });
        throw new NaradClientError('invalid_frame', redactForPublicError(error).message);
      }
      const wireClass = classifyWireRecord(record);
      logNaradSse(record, { frameId: frame.id, event: frame.event, wireClass });
      if (wireClass === 'control') {
        continue;
      }
      if (wireClass === 'authority' || wireClass === 'watch') {
        validateSseAuthorityFrameId(frame, record);
      }
      if (shouldYieldToReducer(record)) {
        onRecord(record);
      }
    }
  }
}

export function createKautukaNaradTransport(options: KautukaNaradTransportOptions): NaradTransport {
  const limits = options.limits ?? DEFAULT_NARAD_LIMITS;
  const abortControllers = new Set<AbortController>();
  let disposed = false;

  function trackAbort(controller: AbortController): AbortSignal {
    abortControllers.add(controller);
    controller.signal.addEventListener(
      'abort',
      () => {
        abortControllers.delete(controller);
      },
      { once: true },
    );
    return controller.signal;
  }

  function linkSignal(controller: AbortController, externalSignal?: AbortSignal): AbortSignal {
    const signal = trackAbort(controller);
    if (externalSignal) {
      if (externalSignal.aborted) {
        controller.abort();
      } else {
        externalSignal.addEventListener('abort', () => controller.abort(), { once: true });
      }
    }
    return signal;
  }

  return {
    async attach(sessionId, attachOptions): Promise<NaradTransportAttachResult> {
      if (disposed) {
        throw new NaradClientError('transport_disposed', 'Transport disposed.');
      }
      const controller = new AbortController();
      const signal = trackAbort(controller);
      const claimedProfiles = attachOptions?.claimedProfiles ?? options.claimedProfiles;
      const workflowPath = workflowBase(options);
      const query = new URLSearchParams();
      if (claimedProfiles && claimedProfiles.length > 0) {
        query.set('claimedProfiles', claimedProfiles.join(','));
      }
      if (!workflowPath) {
        query.set('sessionId', sessionId);
        query.set('agentId', options.agentId ?? '');
      }
      const response = await options.fetch(
        workflowPath ? `${workflowPath}/capabilities?${query.toString()}` : `${NARAD_BASE}/capabilities?${query.toString()}`,
        {
          credentials: 'include',
          signal,
        },
      );
      if (!response.ok) {
        failClosedStatus(response.status);
      }
      const payload = await readJsonRecord(response);
      const capabilities = parseCapabilities(payload);
      return {
        capabilities,
        boundarySessionSequence: capabilities.currentSessionSequence,
      };
    },

    async fetchHistory(
      sessionId,
      afterSessionSequence,
      limit,
      externalSignal,
    ): Promise<SessionHistoryPage> {
      if (disposed) {
        throw new NaradClientError('transport_disposed', 'Transport disposed.');
      }
      const controller = new AbortController();
      const signal = linkSignal(controller, externalSignal);
      const boundedLimit = Math.max(1, Math.min(limit, limits.maxHistoryEvents));
      const workflowPath = workflowBase(options);
      const query = new URLSearchParams({
        afterSessionSequence: String(Math.max(0, afterSessionSequence)),
        limit: String(boundedLimit),
      });
      if (!workflowPath) {
        query.set('agentId', options.agentId ?? '');
      }
      const response = await options.fetch(
        workflowPath
          ? `${workflowPath}/events?${query.toString()}`
          : `${NARAD_BASE}/sessions/${encodeURIComponent(sessionId)}/events?${query.toString()}`,
        { credentials: 'include', signal },
      );
      if (!response.ok) {
        failClosedStatus(response.status);
      }
      const payload = await readJsonRecord(response);
      const events = Array.isArray(payload.events) ? payload.events : [];
      const nextSessionSequence =
        typeof payload.nextSessionSequence === 'number' ? payload.nextSessionSequence : null;
      return { events, nextSessionSequence };
    },

    async *subscribeLive(sessionId, afterSessionSequence, externalSignal) {
      if (disposed) {
        throw new NaradClientError('transport_disposed', 'Transport disposed.');
      }
      let cursor = Math.max(0, afterSessionSequence);
      while (!disposed && !externalSignal?.aborted) {
        const controller = new AbortController();
        const signal = linkSignal(controller, externalSignal);
        const workflowPath = workflowBase(options);
        const query = new URLSearchParams({
          afterSessionSequence: String(cursor),
        });
        if (workflowPath) {
          query.set('hydrate', 'false');
        } else {
          query.set('agentId', options.agentId ?? '');
          query.set('hydrate', 'true');
        }
        let response: Response;
        try {
          response = await options.fetch(
            workflowPath
              ? `${workflowPath}/stream?${query.toString()}`
              : `${NARAD_BASE}/sessions/${encodeURIComponent(sessionId)}/stream?${query.toString()}`,
            { credentials: 'include', signal, headers: { Accept: 'text/event-stream' } },
          );
        } catch (error) {
          if (disposed || signal.aborted || externalSignal?.aborted) {
            return;
          }
          logNaradError('transport', 'live stream fetch failed', error, { sessionId, cursor });
          throw new NaradClientError('live_subscribe_failed', redactForPublicError(error).message);
        }

        const queue: unknown[] = [];
        let resolveNext: ((value: IteratorResult<unknown>) => void) | null = null;
        let streamError: Error | null = null;
        let streamDone = false;

        const pushRecord = (record: unknown): void => {
          if (typeof record === 'object' && record !== null) {
            const eventSessionId = (record as { sessionId?: unknown }).sessionId;
            const sessionSequence = (record as { sessionSequence?: unknown }).sessionSequence;
            if (
              typeof sessionSequence === 'number' &&
              (typeof eventSessionId !== 'string' || eventSessionId === sessionId)
            ) {
              cursor = Math.max(cursor, sessionSequence);
            }
          }
          if (resolveNext) {
            const resolve = resolveNext;
            resolveNext = null;
            resolve({ value: record, done: false });
            return;
          }
          queue.push(record);
        };

        void consumeSseResponse(response, signal, limits, pushRecord)
          .then(() => {
            streamDone = true;
            if (resolveNext) {
              const resolve = resolveNext;
              resolveNext = null;
              resolve({ value: undefined, done: true });
            }
          })
          .catch((error: unknown) => {
            streamError = error instanceof Error ? error : new Error(String(error));
            logNaradError('transport', 'live SSE consumer failed', streamError, { sessionId, cursor });
            streamDone = true;
            if (resolveNext) {
              const resolve = resolveNext;
              resolveNext = null;
              resolve({ value: undefined, done: true });
            }
          });

        while (!disposed && !externalSignal?.aborted) {
          if (queue.length > 0) {
            yield queue.shift()!;
            continue;
          }
          if (streamError) {
            throw streamError;
          }
          if (streamDone) {
            break;
          }
          const next = await new Promise<IteratorResult<unknown>>((resolve) => {
            resolveNext = resolve;
          });
          if (next.done) {
            break;
          }
          yield next.value;
        }

        if (streamError) {
          throw streamError;
        }

        if (disposed || externalSignal?.aborted) {
          return;
        }

        // Brief pause so a flapping proxy does not tight-loop attach hydrate.
        await new Promise<void>((resolve) => {
          setTimeout(resolve, 250);
        });
      }
    },

    async sendControl(message: unknown): Promise<void> {
      if (disposed) {
        throw new NaradClientError('transport_disposed', 'Transport disposed.');
      }
      const controller = new AbortController();
      const signal = trackAbort(controller);
      const response = await options.fetch(`${NARAD_BASE}/client/heartbeat`, {
        method: 'POST',
        credentials: 'include',
        signal,
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(message),
      });
      if (!response.ok) {
        failClosedStatus(response.status);
      }
    },

    dispose(): void {
      disposed = true;
      for (const controller of [...abortControllers]) {
        controller.abort();
      }
      abortControllers.clear();
    },
  };
}

export async function fetchKautukaNaradHistoryPages(
  transport: NaradTransport,
  sessionId: string,
  afterSessionSequence: number,
  pageSize: number,
  apply: (record: unknown) => void,
  signal?: AbortSignal,
): Promise<void> {
  let cursor = Math.max(0, afterSessionSequence);
  while (true) {
    const page = await transport.fetchHistory(sessionId, cursor, pageSize, signal);
    for (const record of page.events) {
      apply(record);
    }
    if (page.nextSessionSequence === null) {
      return;
    }
    cursor = page.nextSessionSequence;
  }
}

export async function consumeKautukaNaradWatchRevisions(
  transportOptions: KautukaNaradTransportOptions,
  sessionId: string,
  getCursor: () => number,
  onRevision: (record: unknown) => void,
  signal: AbortSignal,
): Promise<void> {
  const limits = transportOptions.limits ?? DEFAULT_NARAD_LIMITS;

  while (!signal.aborted) {
    try {
      const cursor = Math.max(0, getCursor());
      const query = new URLSearchParams({
        afterSessionSequence: String(cursor),
      });
      if (transportOptions.agentId) {
        query.set('agentId', transportOptions.agentId);
      }
      const response = await transportOptions.fetch(
        `${NARAD_BASE}/sessions/${encodeURIComponent(sessionId)}/watch?${query.toString()}`,
        { credentials: 'include', signal, headers: { Accept: 'text/event-stream' } },
      );
      await consumeSseResponse(response, signal, limits, (record) => {
        if (classifyWireRecord(record) === 'watch') {
          onRevision(record);
        }
      });
    } catch (error) {
      if (signal.aborted) {
        return;
      }
    }

    if (signal.aborted) {
      return;
    }

    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(resolve, 250);
      signal.addEventListener(
        'abort',
        () => {
          clearTimeout(timer);
          reject(new DOMException('Aborted', 'AbortError'));
        },
        { once: true },
      );
    }).catch(() => undefined);
  }
}

import { authenticatedFetch } from '@/lib/authenticatedFetch';
import { queryClient } from '@/lib/queryClient';
import React from 'react';
import { approvalsQueryKeys } from '../api/queryKeys';

const RECONNECT_DELAY_MS = 1000;

export type ApprovalInvalidation = Readonly<{
  epoch: number;
  eventId: string | null;
  eventType: string | null;
  sessionId: string | null;
}>;

type ApprovalInvalidationDelta = {
  type?: string;
  delta?: Array<{
    value?: {
      eventId?: string;
      eventType?: string;
      sessionId?: string | null;
      rootRunId?: string | null;
    };
  }>;
};

function readApprovalInvalidation(payload: ApprovalInvalidationDelta): {
  eventId: string;
  eventType: string;
  sessionId: string | null;
} | null {
  if (payload.type !== 'STATE_DELTA') {
    return null;
  }

  const value = payload.delta?.find((operation) => operation.value?.eventId)?.value;
  if (typeof value?.eventId !== 'string' || value.eventId.length === 0) {
    return null;
  }

  return {
    eventId: value.eventId,
    eventType:
      typeof value.eventType === 'string' && value.eventType.length > 0
        ? value.eventType
        : 'hitl.invalidation',
    sessionId:
      typeof value.sessionId === 'string' && value.sessionId.length > 0 ? value.sessionId : null,
  };
}

/**
 * Subscribes to `/api/approvals/changes` invalidation SSE and bumps an epoch so pending
 * Approvals / nav badge can refetch without clock polling.
 */
export function useApprovalInvalidationSync(
  enabled: boolean,
  scope: 'mine' | 'all' = 'mine'
): ApprovalInvalidation {
  const [invalidation, setInvalidation] = React.useState<ApprovalInvalidation>({
    epoch: 0,
    eventId: null,
    eventType: null,
    sessionId: null,
  });

  React.useEffect(() => {
    if (!enabled) {
      return;
    }

    const abortController = new AbortController();
    let active = true;
    let appliedEventId: string | null = null;

    const accept = (eventId: string, eventType: string, sessionId: string | null) => {
      if (eventId === appliedEventId) {
        return;
      }
      appliedEventId = eventId;
      setInvalidation((current) => ({
        epoch: current.epoch + 1,
        eventId,
        eventType,
        sessionId,
      }));
      void queryClient.invalidateQueries({ queryKey: approvalsQueryKeys.pending() });
      void queryClient.invalidateQueries({ queryKey: approvalsQueryKeys.history() });
      if (sessionId) {
        void queryClient.invalidateQueries({ queryKey: approvalsQueryKeys.bySession(sessionId) });
      }
      void queryClient.invalidateQueries({ queryKey: approvalsQueryKeys.all });
    };

    const consume = async (response: Response) => {
      if (!response.ok || response.body === null) {
        throw new Error(`Approval change stream returned HTTP ${response.status}.`);
      }

      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';
      while (active) {
        const { done, value } = await reader.read();
        if (done) {
          return;
        }

        buffer += decoder.decode(value, { stream: true });
        const frames = buffer.split(/\r?\n\r?\n/);
        buffer = frames.pop() ?? '';
        for (const frame of frames) {
          const data = frame
            .split(/\r?\n/)
            .filter((line) => line.startsWith('data:'))
            .map((line) => line.slice(5).trimStart())
            .join('\n');
          if (data.length === 0) {
            continue;
          }

          const revision = readApprovalInvalidation(JSON.parse(data) as ApprovalInvalidationDelta);
          if (revision !== null) {
            accept(revision.eventId, revision.eventType, revision.sessionId);
          }
        }
      }
    };

    const listen = async () => {
      const path =
        scope === 'all'
          ? '/api/approvals/changes?scope=all'
          : '/api/approvals/changes';
      while (active) {
        try {
          const response = await authenticatedFetch(path, {
            credentials: 'include',
            signal: abortController.signal,
          });
          if ([401, 403].includes(response.status)) {
            return;
          }
          await consume(response);
        } catch {
          if (!active || abortController.signal.aborted) {
            return;
          }
        }

        if (active) {
          await new Promise((resolve) => globalThis.setTimeout(resolve, RECONNECT_DELAY_MS));
        }
      }
    };

    void listen();
    return () => {
      active = false;
      abortController.abort();
    };
  }, [enabled, scope]);

  return invalidation;
}

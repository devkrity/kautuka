import {
  useKautukaNaradConnectionStatus,
  useKautukaNaradSelector,
} from './KautukaNaradProvider';
import { useApprovalsBySession, useApprovalInvalidationSync, approvalsQueryKeys } from '@/features/approvals';
import type { Approval } from '@/features/approvals';
import { queryClient } from '@/lib/queryClient';
import React from 'react';
import {
  mergeOpenInterruptsWithPendingApprovals,
  selectActiveRootGraph,
  selectComposerLocked,
  selectConnectionView,
  selectOpenInterrupts,
  selectPartialAssistantText,
  selectProgressForActiveRoot,
  selectRunSummary,
  selectTranscriptTurns,
  shallowArrayEqual,
  type KautukaConnectionView,
  type KautukaGraphView,
  type KautukaRunSummary,
  type KautukaTranscriptTurn,
} from './selectors';

export {
  selectActiveRootGraph,
  selectComposerLocked,
  selectConnectionView,
  selectOpenInterrupts,
  selectPartialAssistantText,
  selectProgressForActiveRoot,
  selectRunSummary,
  selectTranscriptTurns,
  shallowArrayEqual,
} from './selectors';

export type {
  KautukaConnectionView,
  KautukaGraphView,
  KautukaRunSummary,
  KautukaTranscriptTurn,
} from './selectors';

export function useKautukaTranscriptTurns(): readonly KautukaTranscriptTurn[] {
  const sessionApprovals = useChatSessionApprovals();
  return useKautukaNaradSelector(
    (snapshot) => selectTranscriptTurns(snapshot, sessionApprovals),
    shallowArrayEqual,
  );
}

export function useKautukaPartialAssistantText(): string {
  return useKautukaNaradSelector(selectPartialAssistantText);
}

export function useKautukaRunSummary(): KautukaRunSummary {
  return useKautukaNaradSelector(selectRunSummary, (previous, next) =>
    previous.activeRootRunId === next.activeRootRunId &&
    previous.isRunning === next.isRunning &&
    shallowArrayEqual(previous.nestedRuns, next.nestedRuns),
  );
}

export function useKautukaOpenInterrupts() {
  return useKautukaNaradSelector(selectOpenInterrupts, shallowArrayEqual);
}

export function useKautukaMergedOpenInterrupts(sessionId: string | null) {
  const sessionApprovals = useChatSessionApprovals(sessionId);
  return useKautukaNaradSelector(
    (snapshot) => mergeOpenInterruptsWithPendingApprovals(snapshot, sessionApprovals),
    shallowArrayEqual,
  );
}

function useChatSessionApprovals(sessionIdOverride?: string | null): readonly Approval[] {
  const snapshotSessionId = useKautukaNaradSelector((snapshot) => snapshot.sessionId);
  const sessionId = sessionIdOverride ?? snapshotSessionId;
  const connection = useKautukaConnectionView();
  useApprovalInvalidationSync(Boolean(sessionId));
  const { data: sessionApprovals = [] } = useApprovalsBySession(sessionId, {
    refetchInterval: false,
  });
  React.useEffect(() => {
    if (!sessionId || (!connection.sessionGap && !connection.resyncRequired)) {
      return;
    }
    void queryClient.invalidateQueries({ queryKey: approvalsQueryKeys.bySession(sessionId) });
  }, [sessionId, connection.sessionGap, connection.resyncRequired]);
  return sessionApprovals;
}

export function useKautukaActiveRootGraph(): KautukaGraphView {
  return useKautukaNaradSelector(selectActiveRootGraph, (previous, next) =>
    previous.resyncRequired === next.resyncRequired && previous.graph === next.graph,
  );
}

export function useKautukaProgressEntries() {
  const sessionApprovals = useChatSessionApprovals();
  return useKautukaNaradSelector(
    (snapshot) => selectProgressForActiveRoot(snapshot, sessionApprovals),
    shallowArrayEqual,
  );
}

export function useKautukaConnectionView(): KautukaConnectionView {
  return useKautukaNaradSelector(selectConnectionView, (previous, next) =>
    previous.lastError === next.lastError &&
    previous.resyncRequired === next.resyncRequired &&
    previous.sessionGap === next.sessionGap,
  );
}

export function useKautukaComposerLocked(localDeliveryPending: boolean): boolean {
  const connectionStatus = useKautukaNaradConnectionStatus();
  return useKautukaNaradSelector(
    (snapshot) => selectComposerLocked(snapshot, localDeliveryPending, connectionStatus),
    (previous, next) => previous === next,
  );
}

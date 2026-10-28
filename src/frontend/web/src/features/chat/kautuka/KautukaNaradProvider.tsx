import React from 'react';
import {
  NARAD_PROFILE_IDS,
  createAttachCoordinator,
  installNaradClientDebugGlobal,
  isNaradClientDebugEnabled,
  logNaradDebug,
  logNaradError,
  type NaradClientStore,
  type NaradReducer,
  type AttachCoordinatorSnapshot,
  type IntentAcknowledgment,
} from '@devkrity/narad';
import { NaradProvider, useNaradClientStore, useNaradSelector, useNaradSnapshot } from '@devkrity/narad-react';
import { authenticatedFetch } from '@/lib/authenticatedFetch';
import { approvalsQueryKeys } from '@/features/approvals';
import { queryClient } from '@/lib/queryClient';
import type { ConversationAsset } from '../types/chatTypes';
import { createNaradSessionBinding } from './createNaradSessionBinding';
import {
  consumeKautukaNaradWatchRevisions,
  createKautukaNaradTransport,
  type KautukaNaradTransportOptions,
} from './createKautukaNaradTransport';
import {
  cancelNaradGraphNode,
  resolveNaradInterrupt,
  retryNaradExecutionTarget,
  sendNaradUserMessage,
  stopNaradActiveRoot,
  type NaradIntentResponse,
  type NaradThreadCommandResponse,
} from './kautukaNaradIntents';
import { isNaradIntentAccepted } from './kautukaNaradIntentStatus';
import { readExpectedRevision } from './runSessionCatchUp';

const DEFAULT_CLAIMED_PROFILES = [
  NARAD_PROFILE_IDS.conversation,
  NARAD_PROFILE_IDS.graph,
  NARAD_PROFILE_IDS.progress,
  NARAD_PROFILE_IDS.ui,
] as const;

export type KautukaNaradConnectionStatus = 'idle' | 'attaching' | 'live' | 'error' | 'unauthorized';

export type KautukaNaradIntents = Readonly<{
  sendUserMessage(message: string, assets?: ConversationAsset[]): Promise<NaradIntentResponse>;
  resolveInterrupt(input: {
    interruptId: string;
    decision: string;
    reason?: string;
  }): Promise<NaradThreadCommandResponse>;
  stopActiveRoot(input?: { runId?: string; reason?: string }): Promise<NaradThreadCommandResponse>;
  retryTarget(input: { targetType: string; targetId: string }): Promise<NaradThreadCommandResponse>;
  cancelGraphNode(input: { nodeId: string }): Promise<NaradThreadCommandResponse>;
  abortLocalDelivery(): void;
}>;

export type KautukaNaradContextValue = Readonly<{
  connectionStatus: KautukaNaradConnectionStatus;
  attachSnapshot: AttachCoordinatorSnapshot;
  intents: KautukaNaradIntents;
  localDeliveryPending: boolean;
}>;

export type KautukaNaradProviderProps = Readonly<{
  agentId: string;
  sessionId: string;
  children: React.ReactNode;
}>;

const KautukaNaradContext = React.createContext<KautukaNaradContextValue | null>(null);

function createPublishingReducer(store: NaradClientStore, reducer: NaradReducer): NaradReducer {
  return {
    getSnapshot: () => store.getSnapshot(),
    apply: (value) => store.apply(value),
    clearSessionGap: () => store.clearSessionGap(),
    clearResyncRequired: () => store.clearResyncRequired(),
    setActiveProfiles: (profiles) => store.setActiveProfiles(profiles),
  };
}

export function toIntentAck(response: NaradIntentResponse | NaradThreadCommandResponse): IntentAcknowledgment {
  const accepted = response.status === 'accepted' || response.status === 'already_applied';
  return {
    accepted,
    requestId: 'requestId' in response && response.requestId ? response.requestId : response.status,
    reason: 'detail' in response ? response.detail : undefined,
  };
}

export const KautukaNaradProvider: React.FC<KautukaNaradProviderProps> = ({
  agentId,
  sessionId,
  children,
}) => {
  const [connectionStatus, setConnectionStatus] = React.useState<KautukaNaradConnectionStatus>('idle');
  const [attachSnapshot, setAttachSnapshot] = React.useState<AttachCoordinatorSnapshot>({
    attached: false,
    backfilling: false,
    live: false,
    liveSubscribed: false,
    pendingCount: 0,
    resyncRequired: false,
    liveError: null,
  });
  const [localDeliveryPending, setLocalDeliveryPending] = React.useState(false);

  const sessionBinding = React.useMemo(
    () =>
      createNaradSessionBinding({
        sessionId,
        activeProfiles: [...DEFAULT_CLAIMED_PROFILES],
      }),
    [sessionId],
  );

  const transportOptions = React.useMemo<KautukaNaradTransportOptions>(
    () => ({
      agentId,
      fetch: authenticatedFetch,
      claimedProfiles: [...DEFAULT_CLAIMED_PROFILES],
    }),
    [agentId],
  );

  const coordinatorRef = React.useRef<ReturnType<typeof createAttachCoordinator> | null>(null);
  const coordinatorUnsubscribeRef = React.useRef<(() => void) | null>(null);
  const lifecycleAbortRef = React.useRef<AbortController | null>(null);
  const mountedRef = React.useRef(true);

  const publishAttachSnapshot = React.useCallback(() => {
    const coordinator = coordinatorRef.current;
    if (!coordinator || !mountedRef.current) {
      return;
    }
    setAttachSnapshot(coordinator.getSnapshot());
  }, []);

  const setFailureStatus = React.useCallback((error: unknown) => {
    const message = error instanceof Error ? error.message : String(error);
    logNaradError('transport', 'connection failure', error, { agentId, sessionId });
    setConnectionStatus(/401|403|unauthorized|access denied/i.test(message) ? 'unauthorized' : 'error');
  }, [agentId, sessionId]);

  const syncCoordinatorConnectionStatus = React.useCallback((snapshot: AttachCoordinatorSnapshot) => {
    if (!mountedRef.current) {
      return;
    }
    if (snapshot.liveError) {
      setConnectionStatus(/unauthorized|access denied/i.test(snapshot.liveError) ? 'unauthorized' : 'error');
      return;
    }
    if (snapshot.live) {
      setConnectionStatus('live');
      return;
    }
    if (snapshot.backfilling || snapshot.liveSubscribed || snapshot.resyncRequired) {
      setConnectionStatus('attaching');
    }
  }, []);

  /** History fill only — never second owners of authority. */
  const requestRecovery = React.useCallback(async () => {
    const coordinator = coordinatorRef.current;
    if (!coordinator) {
      return;
    }
    logNaradDebug('intent', 'history catch-up', {
      agentId,
      sessionId,
      lastSessionSequence: sessionBinding.store.getSnapshot().lastSessionSequence,
      sessionGap: sessionBinding.store.getSnapshot().sessionGap,
    });
    await coordinator.requestRecovery();
    if (!mountedRef.current) {
      return;
    }
    publishAttachSnapshot();
    syncCoordinatorConnectionStatus(coordinator.getSnapshot());
  }, [agentId, publishAttachSnapshot, sessionBinding.store, sessionId, syncCoordinatorConnectionStatus]);

  const requestRecoveryRef = React.useRef(requestRecovery);
  requestRecoveryRef.current = requestRecovery;

  const disposeSessionRuntime = React.useCallback(() => {
    lifecycleAbortRef.current?.abort();
    coordinatorUnsubscribeRef.current?.();
    coordinatorUnsubscribeRef.current = null;
    coordinatorRef.current?.dispose();
    coordinatorRef.current = null;
  }, []);

  const attachSession = React.useCallback(async (): Promise<void> => {
    disposeSessionRuntime();
    const lifecycle = new AbortController();
    lifecycleAbortRef.current = lifecycle;

    const transport = createKautukaNaradTransport(transportOptions);
    const publishingReducer = createPublishingReducer(sessionBinding.store, sessionBinding.reducer);
    const coordinator = createAttachCoordinator(transport, publishingReducer, {
      sessionId,
      claimedProfiles: [...DEFAULT_CLAIMED_PROFILES],
    });
    coordinatorRef.current = coordinator;
    coordinatorUnsubscribeRef.current = coordinator.subscribe(() => {
      const snapshot = coordinator.getSnapshot();
      publishAttachSnapshot();
      syncCoordinatorConnectionStatus(snapshot);
    });

    setConnectionStatus('attaching');
    publishAttachSnapshot();

    try {
      await coordinator.attach();
      if (!mountedRef.current || lifecycle.signal.aborted) {
        return;
      }
      publishAttachSnapshot();
      const snapshot = coordinator.getSnapshot();
      if (snapshot.liveError) {
        setConnectionStatus(/unauthorized|access denied/i.test(snapshot.liveError) ? 'unauthorized' : 'error');
        return;
      }
      setConnectionStatus(snapshot.live ? 'live' : snapshot.backfilling ? 'attaching' : 'idle');
    } catch (error) {
      if (!mountedRef.current || lifecycle.signal.aborted) {
        return;
      }
      const storeSnapshot = sessionBinding.store.getSnapshot();
      const hasHistory =
        storeSnapshot.lastSessionSequence > 0 || storeSnapshot.conversation.messages.length > 0;
      if (hasHistory) {
        setConnectionStatus('attaching');
        await requestRecoveryRef.current();
      } else {
        setFailureStatus(error);
      }
      publishAttachSnapshot();
    }
  }, [
    disposeSessionRuntime,
    publishAttachSnapshot,
    sessionBinding.reducer,
    sessionBinding.store,
    sessionId,
    setFailureStatus,
    syncCoordinatorConnectionStatus,
    transportOptions,
  ]);

  const attachSessionRef = React.useRef(attachSession);
  attachSessionRef.current = attachSession;

  React.useEffect(() => {
    installNaradClientDebugGlobal();
    if (isNaradClientDebugEnabled()) {
      console.info(
        '[narad] Client debug logging is ON. Disable with localStorage.setItem("narad:debug","0") or __naradDebug.disable()',
      );
    }
    mountedRef.current = true;
    void attachSessionRef.current();

    const watchAbort = new AbortController();
    const store = sessionBinding.store;

    void consumeKautukaNaradWatchRevisions(
      transportOptions,
      sessionId,
      () => store.getSnapshot().lastSessionSequence,
      (record) => {
        // Watch is a revision notification only — not authority delivery.
        // Live carries sessionSequence-ordered domain events. Only fill history if apply
        // latched a gap (or prior catch-up failed).
        store.apply(record);
        const snap = store.getSnapshot();
        const coordinator = coordinatorRef.current;
        if (snap.sessionGap || coordinator?.getSnapshot().resyncRequired) {
          void requestRecoveryRef.current();
        }
      },
      watchAbort.signal,
    ).catch(() => undefined);

    return () => {
      mountedRef.current = false;
      watchAbort.abort();
      disposeSessionRuntime();
    };
    // Attach identity is agent+session. Callback object identity must not tear down live.
  }, [agentId, sessionId, disposeSessionRuntime, sessionBinding.store, transportOptions]);

  const intents = React.useMemo<KautukaNaradIntents>(
    () => ({
      sendUserMessage: async (message, assets) => {
        setLocalDeliveryPending(true);
        logNaradDebug('intent', 'sendUserMessage', { agentId, sessionId, messageLength: message.length });
        try {
          const result = await sendNaradUserMessage(agentId, sessionId, {
            message,
            expectedRevision: null,
            assetIds: (assets ?? []).map((asset) => asset.id),
          });
          logNaradDebug('intent', 'sendUserMessage result', { agentId, sessionId, result });
          if (isNaradIntentAccepted(result.status)) {
            void queryClient.invalidateQueries({ queryKey: ['session-queue', agentId, sessionId] });
          }
          return result;
        } finally {
          setLocalDeliveryPending(false);
        }
      },
      resolveInterrupt: async (input) => {
        logNaradDebug('intent', 'resolveInterrupt start', { agentId, sessionId, input });
        let result = await resolveNaradInterrupt({
          ...input,
          expectedRevision: readExpectedRevision(sessionBinding.store),
        });

        if (result.status === 'conflict') {
          await requestRecoveryRef.current();
          result = await resolveNaradInterrupt({
            ...input,
            expectedRevision: readExpectedRevision(sessionBinding.store),
          });
        }

        if (result.status === 'conflict') {
          result = await resolveNaradInterrupt({
            ...input,
            expectedRevision: null,
          });
        }

        if (isNaradIntentAccepted(result.status)) {
          void queryClient.invalidateQueries({ queryKey: approvalsQueryKeys.all });
        }

        logNaradDebug('intent', 'resolveInterrupt result', { agentId, sessionId, result });
        return result;
      },
      stopActiveRoot: async (input = {}) =>
        stopNaradActiveRoot(agentId, sessionId, {
          ...input,
          expectedRevision: readExpectedRevision(sessionBinding.store),
        }),
      retryTarget: async (input) =>
        retryNaradExecutionTarget(agentId, sessionId, {
          ...input,
          expectedRevision: readExpectedRevision(sessionBinding.store),
        }),
      cancelGraphNode: async (input) =>
        cancelNaradGraphNode(agentId, sessionId, {
          ...input,
          expectedRevision: readExpectedRevision(sessionBinding.store),
        }),
      abortLocalDelivery: () => {
        setLocalDeliveryPending(false);
      },
    }),
    [agentId, sessionBinding.store, sessionId],
  );

  const contextValue = React.useMemo<KautukaNaradContextValue>(
    () => ({
      connectionStatus,
      attachSnapshot,
      intents,
      localDeliveryPending,
    }),
    [attachSnapshot, connectionStatus, intents, localDeliveryPending],
  );

  return (
    <NaradProvider store={sessionBinding.store}>
      <KautukaNaradContext.Provider value={contextValue}>{children}</KautukaNaradContext.Provider>
    </NaradProvider>
  );
};

export function useKautukaNaradContext(): KautukaNaradContextValue {
  const value = React.useContext(KautukaNaradContext);
  if (value === null) {
    throw new Error('useKautukaNaradContext must be used within KautukaNaradProvider.');
  }
  return value;
}

export function useOptionalKautukaNaradContext(): KautukaNaradContextValue | null {
  return React.useContext(KautukaNaradContext);
}

export function useKautukaNaradSnapshot() {
  return useNaradSnapshot();
}

export function useKautukaNaradSelector<T>(
  selector: (snapshot: ReturnType<typeof useNaradSnapshot>) => T,
  isEqual?: (previous: T, next: T) => boolean,
) {
  return useNaradSelector(selector, isEqual);
}

export function useKautukaNaradIntents(): KautukaNaradIntents {
  return useKautukaNaradContext().intents;
}

export function useKautukaNaradConnectionStatus(): KautukaNaradConnectionStatus {
  return useKautukaNaradContext().connectionStatus;
}

export function useKautukaNaradClientStore(): NaradClientStore {
  return useNaradClientStore();
}

export { readExpectedRevision } from './runSessionCatchUp';

import React from 'react';
import {
  NARAD_PROFILE_IDS,
  createAttachCoordinator,
  redactForPublicError,
  type NaradClientStore,
  type NaradReducer,
  type AttachCoordinatorSnapshot,
} from '@devkrity/narad';
import { NaradProvider, useNaradSelector, useNaradSnapshot } from '@devkrity/narad-react';
import { authenticatedFetch } from '@/lib/authenticatedFetch';
import {
  createNaradSessionBinding,
  createKautukaNaradTransport,
  type KautukaNaradTransportOptions,
} from '@/features/chat';

const WORKFLOW_PROFILES = [NARAD_PROFILE_IDS.workflow, NARAD_PROFILE_IDS.graph, NARAD_PROFILE_IDS.progress] as const;

const WorkflowNaradAttachErrorContext = React.createContext<string | null>(null);

export type WorkflowNaradProviderProps = Readonly<{
  workflowId: string;
  instanceId: string;
  children: React.ReactNode;
}>;

function createPublishingReducer(store: NaradClientStore, reducer: NaradReducer): NaradReducer {
  return {
    getSnapshot: () => store.getSnapshot(),
    apply: (value) => store.apply(value),
    clearSessionGap: () => store.clearSessionGap(),
    clearResyncRequired: () => store.clearResyncRequired(),
    setActiveProfiles: (profiles) => store.setActiveProfiles(profiles),
  };
}

export const WorkflowNaradProvider: React.FC<WorkflowNaradProviderProps> = ({
  workflowId,
  instanceId,
  children,
}) => {
  const sessionBinding = React.useMemo(
    () =>
      createNaradSessionBinding({
        sessionId: instanceId,
        activeProfiles: [...WORKFLOW_PROFILES],
      }),
    [instanceId],
  );
  const [attachError, setAttachError] = React.useState<string | null>(null);

  const transportOptions = React.useMemo<KautukaNaradTransportOptions>(
    () => ({
      fetch: authenticatedFetch,
      claimedProfiles: [...WORKFLOW_PROFILES],
      workflowId,
      instanceId,
    }),
    [instanceId, workflowId],
  );

  React.useEffect(() => {
    const transport = createKautukaNaradTransport(transportOptions);
    const publishingReducer = createPublishingReducer(sessionBinding.store, sessionBinding.reducer);
    const coordinator = createAttachCoordinator(transport, publishingReducer, {
      sessionId: instanceId,
      claimedProfiles: [...WORKFLOW_PROFILES],
    });
    const syncError = () => {
      setAttachError(coordinator.getSnapshot().liveError);
    };
    const unsubscribe = coordinator.subscribe(syncError);
    void coordinator.attach().then(syncError).catch((error: unknown) => {
      setAttachError(redactForPublicError(error).message);
    });
    return () => {
      unsubscribe();
      coordinator.dispose();
    };
  }, [instanceId, sessionBinding.reducer, sessionBinding.store, transportOptions]);

  return (
    <WorkflowNaradAttachErrorContext.Provider value={attachError}>
      <NaradProvider store={sessionBinding.store}>{children}</NaradProvider>
    </WorkflowNaradAttachErrorContext.Provider>
  );
};

export function useWorkflowNaradAttachError(): string | null {
  return React.useContext(WorkflowNaradAttachErrorContext);
}

export function useWorkflowNaradSnapshot() {
  return useNaradSnapshot();
}

export function useWorkflowLiveState(instanceId: string) {
  return useNaradSelector((snapshot) => snapshot.workflows.get(instanceId) ?? null);
}

export type { AttachCoordinatorSnapshot };

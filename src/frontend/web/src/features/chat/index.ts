/**
 * Chat feature public API
 */
export * from './api/queries';
export { SessionQueue } from './components/SessionQueue';
export * from './api/sessions';
export * from './client-core';
export {
  KautukaNaradProvider,
  useKautukaNaradContext,
  useKautukaNaradIntents,
  useKautukaNaradSnapshot,
  useKautukaNaradSelector,
  useKautukaNaradConnectionStatus,
  useKautukaTranscriptTurns,
  useKautukaPartialAssistantText,
  useKautukaRunSummary,
  useKautukaOpenInterrupts,
  useKautukaMergedOpenInterrupts,
  useKautukaActiveRootGraph,
  useKautukaProgressEntries,
  useKautukaConnectionView,
  useKautukaComposerLocked,
  formatNaradIntentFailure,
  isNaradIntentAccepted,
  createNaradSessionBinding,
  createKautukaNaradTransport,
  fetchKautukaNaradHistoryPages,
  selectTranscriptTurns,
  selectRunSummary,
  selectOpenInterrupts,
} from './kautuka';
export type {
  KautukaNaradProviderProps,
  KautukaNaradIntents,
  KautukaNaradConnectionStatus,
  KautukaTranscriptTurn,
  KautukaRunSummary,
  KautukaGraphView,
  KautukaConnectionView,
  KautukaNaradTransportOptions,
} from './kautuka';
export {
  classifyExecutionGraphPresentation,
  executionGraphPanelTitle,
} from './types/executionGraph';
export type {
  ExecutionGraphSnapshot,
  ExecutionGraphNode,
  ExecutionGraphPresentation,
} from './types/executionGraph';
export * from './schemas/sessionSchema';
export * from './utils/resolveWorkspaceFiles';
export * from './utils/workspaceFileReferences';
export * from './utils/chatFormatters';
export * from './utils/sessionTimestamp';
export * from './types/chatTypes';

// Components
export { CopyButton } from './components/CopyButton';
export { ApprovalCard } from './components/ApprovalCard';
export { ThinkingDisclosure } from './components/ThinkingDisclosure';
export { WorkspaceReferenceText } from './components/WorkspaceReferenceText';
export { MessageAttachmentComposer } from './components/MessageAttachmentComposer';
export { ConversationAttachmentChips } from './components/ConversationAttachmentChips';
export { useComposerAttachments } from './hooks/useComposerAttachments';
export { ActivityLedger } from './components/ActivityLedger';
export {
  activityEntryProjectionIdentity,
  activityEntryRenderKey,
  assertUniqueActivityProjection,
  orderActivityEntriesForDisplay,
} from './activity/activityProjection';
export { ExecutionGraphPanel } from './components/ExecutionGraphPanel';
export { RunTraceInspector } from './components/RunTraceInspector';
export * from './api/runTrace';

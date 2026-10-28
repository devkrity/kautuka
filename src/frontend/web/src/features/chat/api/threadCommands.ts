import {
  cancelNaradGraphNode,
  resolveNaradInterrupt,
  retryNaradExecutionTarget,
  stopNaradActiveRoot,
  type NaradThreadCommandResponse,
} from '../kautuka/kautukaNaradIntents';

export type ThreadCommandStatus =
  | 'accepted'
  | 'already_applied'
  | 'not_found'
  | 'forbidden'
  | 'expired'
  | 'conflict'
  | 'unsupported'
  | 'rejected';

export type ThreadCommandResponse = Readonly<{
  status: ThreadCommandStatus;
  decision?: string;
  requestId?: string;
  resolvedCount?: number;
  detail?: string;
}>;

function mapNaradThreadCommand(response: NaradThreadCommandResponse): ThreadCommandResponse {
  return {
    status: response.status,
    decision: response.decision,
    requestId: response.requestId,
    resolvedCount: response.resolvedCount,
    detail: response.detail,
  };
}

export async function resolveThreadInterrupt(
  input: Readonly<{
    interruptId: string;
    decision: string;
    reason?: string;
    expectedRevision?: number;
  }>
): Promise<ThreadCommandResponse> {
  return mapNaradThreadCommand(await resolveNaradInterrupt(input));
}

export async function stopActiveRootCommand(
  agentId: string,
  threadId: string,
  input: Readonly<{ runId?: string; reason?: string; expectedRevision?: number }> = {}
): Promise<ThreadCommandResponse> {
  return mapNaradThreadCommand(await stopNaradActiveRoot(agentId, threadId, input));
}

export async function retryExecutionTargetCommand(
  agentId: string,
  threadId: string,
  input: Readonly<{ targetType: string; targetId: string; expectedRevision?: number }>
): Promise<ThreadCommandResponse> {
  return mapNaradThreadCommand(await retryNaradExecutionTarget(agentId, threadId, input));
}

export async function cancelGraphNodeCommand(
  agentId: string,
  threadId: string,
  input: Readonly<{ nodeId: string; expectedRevision?: number }>
): Promise<ThreadCommandResponse> {
  return mapNaradThreadCommand(await cancelNaradGraphNode(agentId, threadId, input));
}

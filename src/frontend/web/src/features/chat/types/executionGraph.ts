export const NARAD_GRAPH_SCHEMA_VERSION = 'narad.graph/v1' as const;

export type ExecutionGraphPresentation = 'hidden' | 'execution' | 'workflow';

export interface ExecutionGraphActionTarget {
  readonly kind: string;
  readonly id: string;
}

export interface ExecutionGraphNode {
  readonly nodeId: string;
  readonly kind: 'run' | 'tool' | 'agent' | 'approval' | 'phase' | string;
  readonly status: 'pending' | 'running' | 'waiting' | 'completed' | 'failed' | 'cancelled' | string;
  readonly label?: string;
  readonly fanOutGroupId?: string;
  readonly partialOutcome?: { readonly summary?: string; readonly code?: string };
  readonly waitOwner?: { readonly type: string; readonly id: string };
  readonly cancellableTarget?: ExecutionGraphActionTarget;
  readonly retryTarget?: ExecutionGraphActionTarget;
}

export interface ExecutionGraphEdge {
  readonly edgeId: string;
  readonly fromNodeId: string;
  readonly toNodeId: string;
  readonly kind: string;
}

export interface ExecutionGraphSnapshot {
  readonly schemaVersion: string;
  readonly executionId: string;
  readonly revision: number;
  readonly rootNodeId: string;
  readonly nodes: readonly ExecutionGraphNode[];
  readonly edges: readonly ExecutionGraphEdge[];
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function readNodeId(node: Record<string, unknown>): string | null {
  if (typeof node.nodeId === 'string' && node.nodeId.length > 0) {
    return node.nodeId;
  }
  if (typeof node.id === 'string' && node.id.length > 0) {
    return node.id;
  }
  return null;
}

function readActionTarget(value: unknown): ExecutionGraphActionTarget | undefined {
  if (!isRecord(value) || typeof value.kind !== 'string' || typeof value.id !== 'string') {
    return undefined;
  }
  return { kind: value.kind, id: value.id };
}

export function normalizeExecutionGraphNode(value: unknown): ExecutionGraphNode | null {
  if (!isRecord(value)) {
    return null;
  }
  const nodeId = readNodeId(value);
  if (!nodeId) {
    return null;
  }
  return {
    nodeId,
    kind: typeof value.kind === 'string' ? value.kind : 'tool',
    status: typeof value.status === 'string' ? value.status : 'pending',
    label: typeof value.label === 'string' ? value.label : undefined,
    fanOutGroupId: typeof value.fanOutGroupId === 'string' ? value.fanOutGroupId : undefined,
    partialOutcome: isRecord(value.partialOutcome)
      ? {
          summary: typeof value.partialOutcome.summary === 'string' ? value.partialOutcome.summary : undefined,
          code: typeof value.partialOutcome.code === 'string' ? value.partialOutcome.code : undefined,
        }
      : undefined,
    waitOwner: isRecord(value.waitOwner) &&
      typeof value.waitOwner.type === 'string' &&
      typeof value.waitOwner.id === 'string'
      ? { type: value.waitOwner.type, id: value.waitOwner.id }
      : undefined,
    cancellableTarget: readActionTarget(value.cancellableTarget),
    retryTarget: readActionTarget(value.retryTarget),
  };
}

export function normalizeExecutionGraphEdge(value: unknown): ExecutionGraphEdge | null {
  if (!isRecord(value)) {
    return null;
  }
  const edgeId =
    typeof value.edgeId === 'string'
      ? value.edgeId
      : typeof value.id === 'string'
        ? value.id
        : null;
  const fromNodeId =
    typeof value.fromNodeId === 'string'
      ? value.fromNodeId
      : typeof value.from === 'string'
        ? value.from
        : null;
  const toNodeId =
    typeof value.toNodeId === 'string'
      ? value.toNodeId
      : typeof value.to === 'string'
        ? value.to
        : null;
  if (!edgeId || !fromNodeId || !toNodeId || typeof value.kind !== 'string') {
    return null;
  }
  return {
    edgeId,
    fromNodeId,
    toNodeId,
    kind: value.kind,
  };
}

export function buildExecutionGraphSnapshot(input: {
  rootRunId: string;
  revision: number;
  rootNodeId: string;
  nodes: readonly unknown[];
  edges: readonly unknown[];
}): ExecutionGraphSnapshot | null {
  const nodes = input.nodes
    .map(normalizeExecutionGraphNode)
    .filter((node): node is ExecutionGraphNode => node !== null);
  const edges = input.edges
    .map(normalizeExecutionGraphEdge)
    .filter((edge): edge is ExecutionGraphEdge => edge !== null);
  if (nodes.length === 0) {
    return null;
  }
  return {
    schemaVersion: NARAD_GRAPH_SCHEMA_VERSION,
    executionId: input.rootRunId,
    revision: input.revision,
    rootNodeId: input.rootNodeId,
    nodes,
    edges,
  };
}

const workflowEdgeKinds = new Set(['parent_child', 'parent-child', 'fan_out', 'join', 'sequence']);

function hasWorkflowTopology(graph: ExecutionGraphSnapshot): boolean {
  if (graph.nodes.some((node) => node.kind === 'agent')) {
    return true;
  }
  if (graph.nodes.some((node) => node.kind === 'run' && node.nodeId !== graph.rootNodeId)) {
    return true;
  }
  if (graph.nodes.some((node) => node.kind === 'phase' || node.nodeId.startsWith('join:'))) {
    return true;
  }
  if (graph.nodes.some((node) => Boolean(node.fanOutGroupId))) {
    return true;
  }
  if (graph.edges.some((edge) => workflowEdgeKinds.has(edge.kind))) {
    return true;
  }
  return false;
}

export function classifyExecutionGraphPresentation(
  graph: ExecutionGraphSnapshot | null,
): ExecutionGraphPresentation {
  if (!graph) {
    return 'hidden';
  }
  const detailNodes = graph.nodes.filter((node) => node.nodeId !== graph.rootNodeId);
  if (detailNodes.length === 0) {
    return 'hidden';
  }
  // Nested AAAT / multi-agent topology is product "workflow" — chat must not advertise Workflow
  // progress until that UI is designed. Flat single-agent tool/approval chains may still show
  // as execution progress.
  if (hasWorkflowTopology(graph)) {
    return 'hidden';
  }
  return 'execution';
}

export function executionGraphPanelTitle(presentation: ExecutionGraphPresentation): string {
  switch (presentation) {
    case 'workflow':
      return 'Workflow progress';
    case 'execution':
      return 'Execution progress';
    default:
      return 'Execution progress';
  }
}

import '@xyflow/react/dist/style.css';

import * as Popover from '@radix-ui/react-popover';
import ELK, { type ElkNode } from 'elkjs/lib/elk-api.js';
import elkWorkerUrl from 'elkjs/lib/elk-worker.min.js?url';
import {
  Background,
  BaseEdge,
  Controls,
  EdgeLabelRenderer,
  Handle,
  MarkerType,
  Position,
  ReactFlow,
  useReactFlow,
  type Edge,
  type EdgeProps,
  type Node,
  type NodeProps,
} from '@xyflow/react';
import { useEffect, useMemo, useRef, useState } from 'react';
import React from 'react';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select } from '@/components/ui/select';
import { Textarea } from '@/components/ui/textarea';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Plus, Trash2, X } from 'lucide-react';
import {
  addStartField,
  createFieldDraft,
  createHandoffDraft,
  automaticHandoffs,
  createStepDraft,
  updateStepResults,
  workflowChoiceOps,
  fieldDisplayName,
  fieldKeyFromLabel,
  type WorkflowFieldDraft,
  type WorkflowStepDraft,
} from '../model/workflowDraft';
import {
  addChoice,
  addChoiceRule,
  addStepOn,
  addPickListAnswer,
  connectPathTo,
  disconnectPath,
  draftToGraph,
  effectiveTransitions,
  guaranteedFields,
  joinCandidates,
  moveRule,
  planRemoveChoiceRule,
  replaceAgent,
  workflowDisplayNumbers,
  type ConnectionRef,
  planDeleteStep,
  type DeletePlan,
  type EditResult,
  type WorkflowGraphNode,
  type WorkflowTransition,
} from '../model/workflowCanvas';

const STEP_WIDTH = 160;
const STEP_HEIGHT = 144;
const START_WIDTH = 220;
const START_HEIGHT = 100;
const OUTPUT_HANDLE_ID = 'out-main';
type CanvasAgent = { id: string; name: string; icon?: string; color?: string };
let elk: InstanceType<typeof ELK> | null = null;

interface WorkflowLayoutNode {
  x: number;
  y: number;
  width: number;
  height: number;
  sourceHandles: Record<string, { x: number; y: number }>;
  targetHandles: Record<string, { x: number; y: number }>;
}

interface WorkflowLayoutEdge {
  path: string;
  labelPosition: { x: number; y: number };
  labelWidth: number;
  labelHidden: boolean;
  actionHidden: boolean;
}

interface WorkflowCanvasLayout {
  nodes: Record<string, WorkflowLayoutNode>;
  edges: Record<string, WorkflowLayoutEdge>;
}

interface WorkflowCanvasLayoutState {
  signature: string;
  topologySignature: string;
  layout: WorkflowCanvasLayout;
}

interface CanvasNodeData extends Record<string, unknown> {
  title: string;
  preview: string;
  promptTemplate: string;
  agentName: string;
  agentIcon?: string;
  agentColor?: string;
  results: string[];
  requiresReview: boolean;
  selected: boolean;
  start: boolean;
  terminal: boolean;
  branchHandles: string[];
  sourceHandlePositions: Record<string, { x: number; y: number }>;
  targetHandlePositions: Record<string, { x: number; y: number }>;
  boxHeight: number;
  badges: string[];
  problemMessage?: string;
  fields: WorkflowFieldDraft[];
  onSelect: (key: string) => void;
  agents: CanvasAgent[];
  onExtendPath: (key: string, agentId: string) => void;
  onAddChoice: (key: string) => void;
  onEditResult: (result: EditResult) => void;
  onHoverTarget: (key: string | null) => void;
  joinTargets: Array<{ key: string; name: string }>;
  onConnectPathTo: (source: string, target: string) => void;
  onToggleReview: (key: string, value: boolean) => void;
  key: string;
  readOnly: boolean;
}

type CanvasFlowNode = Node<CanvasNodeData, 'workflow-step'>;

interface CanvasEdgeData extends Record<string, unknown> {
  label: string;
  startEdge?: boolean;
  kind: WorkflowTransition['kind'];
  path: string;
  labelPosition: { x: number; y: number };
  labelWidth: number;
  sourceStepKey: string;
  targetStepKey: string;
  addRef: ConnectionRef;
  addLabel: string;
  agents: CanvasAgent[];
  onAddStep: (ref: ConnectionRef, agentId: string) => void;
  onAddChoice: (ref: ConnectionRef) => void;
  onConnectPathTo: (source: string, target: string) => void;
  joinTargets: Array<{ key: string; name: string }>;
  onHoverTarget: (key: string | null) => void;
  readOnly: boolean;
  hideAddAction?: boolean;
}

type CanvasFlowEdge = Edge<CanvasEdgeData, 'workflow-path'>;

const nodeTypes = { 'workflow-step': WorkflowBox };
const edgeTypes = { 'workflow-path': WorkflowPathEdge };

function renderPromptTemplate(
  template: string,
  fields: WorkflowFieldDraft[] = []
): React.ReactNode[] {
  return template.split(/\{\{\s*([^{}]+?)\s*\}\}/g).map((part, index) =>
    index % 2 === 1 ? (
      <span key={index} className="workflow-canvas-mention">
        {fieldDisplayName(
          fields.find((field) => field.name === part.trim()) ?? { name: part.trim() }
        )}
      </span>
    ) : (
      part
    )
  );
}

function WorkflowBox({ data }: NodeProps<CanvasFlowNode>) {
  return (
    <div className="workflow-canvas-node">
      <div
        role="group"
        className={`workflow-canvas-box ${data.start ? 'workflow-canvas-start' : ''} ${data.selected ? 'workflow-canvas-selected' : ''}`}
        style={data.agentColor ? { borderLeft: `4px solid ${data.agentColor}` } : undefined}
        aria-label={data.title}
        title={data.title}
        onClick={() => data.onSelect(data.key)}
      >
        {data.start ? (
          <button
            type="button"
            className="workflow-canvas-start-open"
            aria-label="Start"
            onClick={(event) => {
              event.stopPropagation();
              data.onSelect(data.key);
            }}
          >
            <strong>Start</strong>
            <span className="workflow-canvas-start-fields">
              {data.fields.length
                ? `Input: ${data.fields.map(fieldDisplayName).join(', ')}`
                : 'No start fields'}
            </span>
          </button>
        ) : (
          <>
            <button
              type="button"
              className="workflow-canvas-box-open"
              aria-label={data.title}
              onClick={(event) => {
                event.stopPropagation();
                data.onSelect(data.key);
              }}
            >
              <span className="workflow-canvas-step-heading">
                <span className="workflow-canvas-step-number">{data.title.split(' · ')[0]}</span>
                {data.agentIcon ? (
                  <span className="workflow-canvas-agent-icon" aria-hidden="true">
                    {data.agentIcon}
                  </span>
                ) : null}
                <strong>{data.agentName || 'Choose who does this'}</strong>
              </span>
              <span className="workflow-canvas-preview">
                {data.promptTemplate
                  ? renderPromptTemplate(data.promptTemplate, data.fields)
                  : null}
              </span>
              <span className="workflow-canvas-results">
                {data.results.map((result) => (
                  <span key={result}>{result}</span>
                ))}
              </span>
            </button>
            <span className="workflow-canvas-badges">
              {data.badges.map((badge) =>
                badge === '⚠' ? (
                  <span
                    key={badge}
                    className="problem"
                    role="img"
                    aria-label={data.problemMessage || 'This step needs attention.'}
                    title={data.problemMessage || 'This step needs attention.'}
                  >
                    {badge}
                  </span>
                ) : (
                  <span key={badge} className={badge.startsWith('Step ') ? 'problem' : ''}>
                    {badge}
                  </span>
                )
              )}
            </span>
          </>
        )}
      </div>
      {!data.start && data.terminal && !data.readOnly ? (
        <div className="workflow-canvas-add-terminal" onClick={(event) => event.stopPropagation()}>
          <AddStepAction
            label="What happens next?"
            agents={data.agents}
            onAdd={(agentId) => data.onExtendPath(data.key, agentId)}
            onAddChoice={() => data.onAddChoice(data.key)}
            joinTargets={data.joinTargets}
            onConnect={(target) => data.onConnectPathTo(data.key, target)}
            onHoverTarget={data.onHoverTarget}
            openUp
          />
        </div>
      ) : null}
      {!data.start
        ? Object.entries(data.targetHandlePositions).map(([id, position]) => (
            <Handle
              key={id}
              type="target"
              position={position.y > data.boxHeight / 2 ? Position.Bottom : Position.Top}
              id={id}
              style={{ left: position.x, top: position.y, bottom: 'auto' }}
            />
          ))
        : null}
      {data.branchHandles.map((handle) => (
        <Handle
          key={handle}
          type="source"
          position={Position.Bottom}
          id={handle}
          style={{
            left: data.sourceHandlePositions[handle]?.x,
            top: data.sourceHandlePositions[handle]?.y,
            bottom: 'auto',
          }}
        />
      ))}
    </div>
  );
}

function stepEdgeLabel(
  steps: WorkflowStepDraft[],
  transition: WorkflowTransition,
  inputFields: WorkflowFieldDraft[] = []
): string {
  if (transition.kind === 'next') return '';
  if (transition.kind === 'continue') return '';
  if (transition.kind === 'otherwise') return 'Otherwise';
  const step = steps.find((candidate) => candidate.key === transition.source);
  const rule = step?.choices.find((candidate) => candidate.key === transition.ruleKey);
  if (!rule) return 'If';
  const operation =
    workflowChoiceOps.find((candidate) => candidate.id === rule.op)?.label ?? rule.op;
  const value = rule.op === 'exists' ? '' : ` ${rule.value || '…'}`;
  const field = [...inputFields, ...steps.flatMap((candidate) => candidate.writes)].find(
    (candidate) => candidate.name === rule.field
  );
  return `If ${field ? fieldDisplayName(field) : fieldDisplayName({ name: rule.field }) || 'field'} ${operation}${value}`;
}

function edgeLabelWidth(label: string, reserveAddAction = true) {
  const actionWidth = reserveAddAction ? 31 : 0;
  if (!label) return reserveAddAction ? 36 : 0;
  return Math.min(205, label.length * 9 + 24) + actionWidth;
}

function edgeLabelHeight(label: string, reserveAddAction = true) {
  if (!label) return reserveAddAction ? 32 : 0;
  const width = edgeLabelWidth(label, reserveAddAction) - (reserveAddAction ? 31 : 0);
  const charactersPerLine = Math.max(8, Math.floor((width - 14) / 7));
  const textHeight = Math.max(1, wrappedLineCount(label, charactersPerLine)) * 16 + 6;
  return Math.max(26, textHeight) + 6;
}

function wrappedLineCount(text: string, charactersPerLine: number): number {
  if (!text.trim()) return 0;
  return text.split('\n').reduce((total, line) => {
    const words = line.split(/\s+/).filter(Boolean);
    let lines = 1;
    let characters = 0;
    for (const word of words) {
      if (!characters) characters = word.length;
      else if (characters + word.length + 1 <= charactersPerLine) characters += word.length + 1;
      else {
        lines += Math.ceil(word.length / charactersPerLine);
        characters = word.length % charactersPerLine || charactersPerLine;
      }
    }
    return total + lines;
  }, 0);
}

function stepProblemBadges(problems: WorkflowGraphNode['problems']): string[] {
  const shortLabels: Record<string, string> = {
    'missing-instruction': 'Add an instruction',
    'incomplete-check': 'Set up this check',
    'missing-result': 'Add a result to pass on',
    'missing-field-label': 'Add a field name',
  };
  return [
    ...new Set(
      problems.flatMap((problem) => {
        if (shortLabels[problem.code]) return [shortLabels[problem.code]!];
        if (problem.severity === 'error') return ['⚠'];
        return [];
      })
    ),
  ];
}

function stepBoxHeight(node: WorkflowGraphNode) {
  const contentWidth = STEP_WIDTH - 32;
  const charsPerLine = Math.max(8, Math.floor(contentWidth / 9));
  const agentName = node.displayName.split(' · ').slice(1).join(' · ') || 'Choose who does this';
  const agentLines = Math.min(2, wrappedLineCount(agentName, charsPerLine));
  const instructionLines = Math.min(3, wrappedLineCount(node.instructionPreview, charsPerLine));
  const resultLines = node.step.writes
    .filter((field) => field.name.trim() && !(field.label !== undefined && !field.label.trim()))
    .reduce((total, field) => {
      const kind =
        field.enumValues !== undefined
          ? 'pick-list'
          : field.type === 'boolean'
            ? 'yes/no'
            : field.type === 'string'
              ? 'text'
              : 'number';
      const label = `${fieldDisplayName(field)}${kind === 'text' ? '' : ` · ${kind}`}`;
      return total + Math.max(1, wrappedLineCount(label, charsPerLine));
    }, 0);
  const todoLines = stepProblemBadges(node.problems).reduce(
    (total, badge) =>
      total + (badge === '⚠' ? 1 : Math.max(1, wrappedLineCount(badge, charsPerLine))),
    node.step.requiresReview ? 1 : 0
  );
  return Math.max(
    STEP_HEIGHT,
    88 + agentLines * 20 + instructionLines * 20 + resultLines * 21 + todoLines * 18
  );
}

function flowLayoutSignature(
  graph: ReturnType<typeof draftToGraph>,
  steps: WorkflowStepDraft[],
  inputFields: WorkflowFieldDraft[],
  reserveAddActions: boolean
) {
  return JSON.stringify({
    reserveAddActions,
    startTarget: steps[0]?.key ?? null,
    startFields: inputFields.map(fieldDisplayName),
    nodes: graph.nodes.map((node) => ({
      key: node.key,
      displayName: node.displayName,
      instructionPreview: node.instructionPreview,
      problems: node.problems.map((problem) => [problem.code, problem.message, problem.severity]),
      results: node.step.writes.map(fieldDisplayName),
      requiresReview: node.step.requiresReview,
      terminal: node.isTerminal,
      handles: [OUTPUT_HANDLE_ID],
    })),
    edges: graph.connections.map((transition, index) => {
      const label = stepEdgeLabel(steps, transition, inputFields);
      return {
        id: `path-${index}-${transition.source}-${transition.target}`,
        source: transition.source,
        target: transition.target,
        handle: OUTPUT_HANDLE_ID,
        kind: transition.kind,
        label,
        labelWidth: edgeLabelWidth(label, reserveAddActions),
      };
    }),
  });
}

function flowTopologySignature(
  graph: ReturnType<typeof draftToGraph>,
  reserveAddActions: boolean,
  overview: boolean
) {
  return JSON.stringify({
    reserveAddActions,
    overview,
    nodes: graph.nodes.map((node) => node.key),
    connections: graph.connections.map((transition) => [
      transition.source,
      transition.target,
      transition.kind,
      transition.ruleKey ?? '',
    ]),
  });
}

async function layoutWorkflow(
  graph: ReturnType<typeof draftToGraph>,
  steps: WorkflowStepDraft[],
  inputFields: WorkflowFieldDraft[],
  reserveAddActions: boolean
): Promise<WorkflowCanvasLayout> {
  elk ??= new ELK({ workerUrl: elkWorkerUrl });
  const startId = '__workflow_start__';
  const sourcePortId = (nodeKey: string, handle: string) => `source-${nodeKey}-${handle}`;
  const edgeSpecs = [
    ...(steps[0]
      ? [
          {
            id: `${startId}-${steps[0].key}`,
            source: startId,
            target: steps[0].key,
            sourceHandle: OUTPUT_HANDLE_ID,
            sourcePort: sourcePortId(startId, OUTPUT_HANDLE_ID),
            targetHandle: `target-${startId}-${steps[0].key}`,
            label: '',
            kind: 'start' as const,
          },
        ]
      : []),
    ...graph.connections.map((transition, index) => {
      const label = stepEdgeLabel(steps, transition, inputFields);
      return {
        id: `path-${index}-${transition.source}-${transition.target}`,
        source: transition.source,
        target: transition.target,
        sourceHandle: OUTPUT_HANDLE_ID,
        sourcePort: sourcePortId(transition.source, OUTPUT_HANDLE_ID),
        targetHandle: `target-path-${index}-${transition.source}-${transition.target}`,
        label,
        kind: transition.kind,
      };
    }),
  ];
  const nodeSpecs = [
    { id: startId, width: START_WIDTH, height: START_HEIGHT },
    ...graph.nodes.map((node) => ({
      id: node.key,
      width: STEP_WIDTH,
      height: stepBoxHeight(node),
    })),
  ];
  const elkGraph: ElkNode = {
    id: 'workflow',
    children: nodeSpecs.map((node) => {
      const incomingEdges = edgeSpecs.filter((edge) => edge.target === node.id);
      const targetPorts = incomingEdges.length
        ? incomingEdges.map((edge) => ({
            id: edge.targetHandle,
            width: 8,
            height: 8,
            layoutOptions: { 'org.eclipse.elk.port.borderOffset': '-8' },
          }))
        : node.id === startId
          ? []
          : [
              {
                id: `target-${node.id}-unconnected`,
                width: 8,
                height: 8,
                layoutOptions: { 'org.eclipse.elk.port.borderOffset': '-8' },
              },
            ];
      const sourcePorts = [
        {
          id: sourcePortId(node.id, OUTPUT_HANDLE_ID),
          width: 8,
          height: 8,
          layoutOptions: { 'org.eclipse.elk.port.borderOffset': '-8' },
        },
      ];
      return {
        ...node,
        ports: [...targetPorts, ...sourcePorts],
      };
    }),
    edges: edgeSpecs.map((edge) => {
      return {
        id: edge.id,
        sources: [edge.sourcePort],
        targets: [edge.targetHandle],
        labels:
          edge.kind !== 'start' && (edge.label || reserveAddActions)
            ? [
                {
                  id: `label-${edge.id}`,
                  text: edge.label || '\u00a0',
                  width: edgeLabelWidth(edge.label, reserveAddActions),
                  height: edgeLabelHeight(edge.label, reserveAddActions),
                },
              ]
            : [],
      };
    }),
    layoutOptions: {
      'elk.algorithm': 'layered',
      'elk.direction': 'DOWN',
      'elk.edgeRouting': 'ORTHOGONAL',
      'elk.spacing.nodeNode': '8',
      'elk.spacing.edgeLabel': '8',
      'elk.layered.spacing.nodeNodeBetweenLayers': '24',
      'elk.layered.spacing.edgeNodeBetweenLayers': '32',
      'elk.layered.spacing.edgeEdgeBetweenLayers': '20',
      'elk.layered.nodePlacement.strategy': 'BRANDES_KOEPF',
      'elk.layered.layering.strategy': 'LONGEST_PATH_SOURCE',
      'elk.layered.nodePlacement.bk.fixedAlignment': 'BALANCED',
      'elk.layered.considerModelOrder.strategy': 'PREFER_EDGES',
      'elk.padding': '[top=20,left=24,bottom=20,right=24]',
    },
  };
  const result = await elk.layout(elkGraph);
  const layoutNodes: WorkflowCanvasLayout['nodes'] = {};
  for (const node of result.children ?? []) {
    const portPosition = (port: NonNullable<typeof node.ports>[number]) => ({
      x: (port.x ?? 0) + (port.width ?? 0) / 2,
      y: (port.y ?? 0) + (port.height ?? 0) / 2,
    });
    const sourceHandles: WorkflowLayoutNode['sourceHandles'] = {};
    const targetHandles: WorkflowLayoutNode['targetHandles'] = {};
    for (const port of node.ports ?? []) {
      const sourcePrefix = `source-${node.id}-`;
      if (port.id.startsWith(sourcePrefix))
        sourceHandles[port.id.slice(sourcePrefix.length)] = portPosition(port);
      else targetHandles[port.id] = portPosition(port);
    }
    layoutNodes[node.id] = {
      x: node.x ?? 0,
      y: node.y ?? 0,
      width: nodeSpecs.find((spec) => spec.id === node.id)?.width ?? STEP_WIDTH,
      height: nodeSpecs.find((spec) => spec.id === node.id)?.height ?? STEP_HEIGHT,
      sourceHandles,
      targetHandles,
    };
  }
  const layoutEdges: WorkflowCanvasLayout['edges'] = {};
  for (const edge of result.edges ?? []) {
    const section = edge.sections?.[0];
    const path = section ? elkOrthogonalPath(section) : '';
    if (!section) continue;
    const label = edge.labels?.[0];
    const middle = {
      x: (section.startPoint.x + section.endPoint.x) / 2,
      y: (section.startPoint.y + section.endPoint.y) / 2,
    };
    layoutEdges[edge.id] = {
      path,
      labelPosition: label
        ? {
            x: (label.x ?? middle.x) + (label.width ?? 0) / 2,
            y: (label.y ?? middle.y) + (label.height ?? 0) / 2,
          }
        : middle,
      labelWidth:
        label?.width ??
        edgeLabelWidth(
          edgeSpecs.find((spec) => spec.id === edge.id)?.label ?? '',
          reserveAddActions
        ),
      labelHidden: false,
      actionHidden: false,
    };
  }
  if (
    Object.keys(layoutNodes).length !== nodeSpecs.length ||
    Object.keys(layoutEdges).length !== edgeSpecs.length
  ) {
    throw new Error('The workflow layout is incomplete.');
  }
  return { nodes: layoutNodes, edges: layoutEdges };
}

function elkOrthogonalPath(section: {
  startPoint: { x: number; y: number };
  bendPoints?: Array<{ x: number; y: number }>;
  endPoint: { x: number; y: number };
}) {
  const points = [section.startPoint, ...(section.bendPoints ?? []), section.endPoint];
  let path = `M ${points[0]!.x} ${points[0]!.y}`;
  const cornerRadius = 8;
  for (let index = 1; index < points.length - 1; index += 1) {
    const previous = points[index - 1]!;
    const corner = points[index]!;
    const next = points[index + 1]!;
    const previousLength = Math.hypot(corner.x - previous.x, corner.y - previous.y);
    const nextLength = Math.hypot(next.x - corner.x, next.y - corner.y);
    const radius = Math.min(cornerRadius, previousLength / 2, nextLength / 2);
    if (radius === 0) continue;
    const before = {
      x: corner.x + ((previous.x - corner.x) * radius) / previousLength,
      y: corner.y + ((previous.y - corner.y) * radius) / previousLength,
    };
    const after = {
      x: corner.x + ((next.x - corner.x) * radius) / nextLength,
      y: corner.y + ((next.y - corner.y) * radius) / nextLength,
    };
    path += ` L ${before.x} ${before.y} Q ${corner.x} ${corner.y} ${after.x} ${after.y}`;
  }
  const end = points[points.length - 1]!;
  return `${path} L ${end.x} ${end.y}`;
}

function formatChoiceSentence(
  choice: WorkflowStepDraft['choices'][number],
  result?: WorkflowFieldDraft
): string {
  const fieldName = result
    ? fieldDisplayName(result) || 'Add a name for this field'
    : fieldDisplayName({ name: choice.field });
  if (!choice.field) return 'If … (finish this)';
  if (!result) return `If ${fieldName} …`;
  if (result.type === 'boolean') {
    if (choice.op === 'exists') return `If ${fieldName} has a value`;
    return `If ${fieldName} ${choice.value === 'false' || choice.op === 'ne' ? 'is No' : 'is Yes'}`;
  }
  if (result.enumValues?.length) return `If ${fieldName} is ${choice.value || '…'}`;
  if (choice.op === 'exists') return `If ${fieldName} has a value`;
  const operation = workflowChoiceOps.find((candidate) => candidate.id === choice.op)?.label ?? '…';
  return `If ${fieldName} ${operation}${choice.value ? ` ${choice.value}` : ' …'}`;
}

function ReadOnlyStepSummary({
  step,
  steps,
  inputFields,
  agents,
}: {
  step: WorkflowStepDraft;
  steps: WorkflowStepDraft[];
  inputFields: WorkflowFieldDraft[];
  agents: CanvasAgent[];
}) {
  const allFields = [...inputFields, ...steps.flatMap((candidate) => candidate.writes)];
  const graph = draftToGraph(steps, inputFields, agents);
  const displayStep = (key: string | undefined) => {
    if (!key) return 'Finishes here';
    const node = graph.nodes.find((candidate) => candidate.key === key);
    if (!node) return 'Unknown step';
    const agentName = agents.find((agent) => agent.id === node.step.agentId)?.name;
    return `Step ${workflowDisplayNumbers(steps).get(key)} · ${agentName ?? 'No one assigned'}`;
  };
  const resultForChoice = (field: string) =>
    allFields.find((candidate) => candidate.name === field);
  const transitions = effectiveTransitions(steps);
  const elseTarget = step.otherwiseKey;
  return (
    <section aria-label="Step summary" className="space-y-4">
      <h2 className="text-base font-semibold">{displayStep(step.key)}</h2>
      <div>
        <h3 className="text-sm font-medium">Who does this</h3>
        <p className="text-sm">
          {agents.find((agent) => agent.id === step.agentId)?.name ??
            'The agent for this step was removed. Use Edit workflow to choose another.'}
        </p>
      </div>
      <div>
        <h3 className="text-sm font-medium">Instruction</h3>
        <p className="whitespace-pre-wrap text-sm">
          {renderPromptTemplate(step.promptTemplate, allFields)}
        </p>
      </div>
      <div>
        <h3 className="text-sm font-medium">Results</h3>
        {step.writes.filter((field) => field.name.trim() && fieldDisplayName(field).trim())
          .length ? (
          <ul className="list-inside list-disc text-sm">
            {step.writes
              .filter((field) => field.name.trim() && fieldDisplayName(field).trim())
              .map((field) => (
                <li key={field.key}>{fieldDisplayName(field)}</li>
              ))}
          </ul>
        ) : (
          <p className="text-sm text-muted-foreground">No results passed on.</p>
        )}
      </div>
      {step.choices.length ? (
        <div>
          <h3 className="text-sm font-medium">Checks</h3>
          <ul className="space-y-2 text-sm">
            {step.choices.map((choice) => {
              const target = transitions.find(
                (transition) =>
                  transition.source === step.key &&
                  transition.kind === 'rule' &&
                  transition.ruleKey === choice.key
              )?.target;
              return (
                <li key={choice.key}>
                  {formatChoiceSentence(choice, resultForChoice(choice.field))}, then{' '}
                  {displayStep(target)}
                </li>
              );
            })}
            <li>Otherwise, {displayStep(elseTarget)}</li>
          </ul>
        </div>
      ) : null}
    </section>
  );
}

function AddStepAction({
  label,
  agents,
  onAdd,
  onAddChoice,
  joinTargets,
  onConnect,
  onHoverTarget,
  openUp = false,
}: {
  label: string;
  agents: CanvasAgent[];
  onAdd: (agentId: string) => void;
  onAddChoice?: () => void;
  joinTargets?: Array<{ key: string; name: string }>;
  onConnect?: (target: string) => void;
  onHoverTarget?: (key: string | null) => void;
  openUp?: boolean;
}) {
  const [open, setOpen] = useState(false);
  const [mode, setMode] = useState<'options' | 'agent' | 'connect'>('options');
  return (
    <Popover.Root
      open={open}
      onOpenChange={(next) => {
        setOpen(next);
        if (next) setMode('options');
        if (!next) onHoverTarget?.(null);
      }}
    >
      <span className="workflow-canvas-add-action">
        <Popover.Trigger asChild>
          <Button type="button" variant="outline" size="sm" aria-label={label}>
            <Plus className="h-3.5 w-3.5" />
          </Button>
        </Popover.Trigger>
      </span>
      <Popover.Portal>
        <Popover.Content
          className="workflow-canvas-agent-picker"
          side={openUp ? 'top' : 'bottom'}
          sideOffset={6}
          align="start"
          collisionPadding={12}
          onClick={(event) => event.stopPropagation()}
        >
          {mode === 'options' ? (
            <>
              <strong>What happens next?</strong>
              <Button type="button" variant="outline" onClick={() => setMode('agent')}>
                Add a step
              </Button>
              {onAddChoice ? (
                <Button
                  type="button"
                  variant="outline"
                  onClick={() => {
                    onAddChoice();
                    setOpen(false);
                  }}
                >
                  Check something
                </Button>
              ) : null}
              {joinTargets?.length && onConnect ? (
                <Button type="button" variant="outline" onClick={() => setMode('connect')}>
                  Continue with an existing step
                </Button>
              ) : null}
            </>
          ) : mode === 'agent' ? (
            <>
              <strong>Who does this?</strong>
              <Button type="button" variant="ghost" onClick={() => setMode('options')}>
                Back
              </Button>
              {agents.map((agent) => (
                <Button
                  key={agent.id}
                  type="button"
                  variant="outline"
                  onClick={() => {
                    onAdd(agent.id);
                    setOpen(false);
                  }}
                >
                  {agent.name}
                </Button>
              ))}
            </>
          ) : (
            <>
              <strong>Continue with an existing step</strong>
              <p>
                Choose where this path continues. Steps that would create a loop are not available.
              </p>
              <div
                role="listbox"
                aria-label="Continue with an existing step"
                className="grid gap-1"
              >
                {joinTargets?.map((entry) => (
                  <button
                    key={entry.key}
                    type="button"
                    role="option"
                    aria-selected="false"
                    onClick={() => {
                      onConnect?.(entry.key);
                      setOpen(false);
                    }}
                    onMouseEnter={() => onHoverTarget?.(entry.key)}
                    onMouseLeave={() => onHoverTarget?.(null)}
                    onFocus={() => onHoverTarget?.(entry.key)}
                    onBlur={() => onHoverTarget?.(null)}
                  >
                    {entry.name}
                  </button>
                ))}
              </div>
            </>
          )}
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

function WorkflowPathEdge({
  id,
  markerEnd,
  sourceX,
  sourceY,
  targetX,
  targetY,
  style,
  data,
}: EdgeProps<CanvasFlowEdge>) {
  const path = data?.path ?? `M ${sourceX} ${sourceY} L ${targetX} ${targetY}`;
  if (data?.startEdge) return <BaseEdge id={id} path={path} style={style} markerEnd={markerEnd} />;
  if (!data || (!data.label && (data.readOnly || data.hideAddAction)))
    return <BaseEdge id={id} path={path} style={style} markerEnd={markerEnd} />;
  return (
    <>
      <BaseEdge id={id} path={path} style={style} markerEnd={markerEnd} />
      <EdgeLabelRenderer>
        <div
          className={`workflow-canvas-edge-label nodrag nopan ${data?.readOnly ? 'read-only' : ''} ${data?.label ? '' : 'no-label'}`}
          data-edge-id={id}
          data-source-step={data?.sourceStepKey}
          data-target-step={data?.targetStepKey}
          aria-label={data?.label}
          title={data?.label}
          style={{
            position: 'absolute',
            pointerEvents: 'all',
            width: Math.max(30, data?.labelWidth ?? 0),
            transform: `translate(-50%, -50%) translate(${data?.labelPosition.x ?? targetX}px,${data?.labelPosition.y ?? targetY}px)`,
          }}
        >
          {data?.label ? <span>{data.label}</span> : null}
          {data && !data.readOnly && !data.hideAddAction ? (
            <AddStepAction
              label={data.addLabel}
              agents={data.agents}
              onAdd={(agentId) => data.onAddStep(data.addRef, agentId)}
              onAddChoice={() => data.onAddChoice(data.addRef)}
              joinTargets={data.joinTargets}
              onConnect={(target) => data.onConnectPathTo(data.addRef.stepKey, target)}
              onHoverTarget={data.onHoverTarget}
            />
          ) : null}
        </div>
      </EdgeLabelRenderer>
    </>
  );
}

function makeFlow(
  steps: WorkflowStepDraft[],
  inputFields: WorkflowFieldDraft[],
  agents: CanvasAgent[],
  selectedKey: string,
  onSelect: (key: string) => void,
  onAddStep: (ref: ConnectionRef, agentId: string) => void,
  onExtendPath: (key: string, agentId: string) => void,
  onAddChoice: (key: string) => void,
  onAddChoiceOnPath: (ref: ConnectionRef) => void,
  onToggleReview: (key: string, value: boolean) => void,
  onEditResult: (result: EditResult) => void,
  onHoverTarget: (key: string | null) => void,
  governed: boolean,
  showValidation: boolean,
  readOnly: boolean,
  overview: boolean,
  layout: WorkflowCanvasLayout
) {
  const graph = draftToGraph(steps, inputFields, agents, governed, showValidation);
  const startId = '__workflow_start__';
  const startTarget = steps[0]?.key;
  const startLayout = layout.nodes[startId];
  if (!startLayout) return { nodes: [], edges: [] };
  const nodes: CanvasFlowNode[] = [
    {
      id: startId,
      type: 'workflow-step',
      position: { x: startLayout.x, y: startLayout.y },
      width: startLayout.width,
      height: startLayout.height,
      data: {
        key: startId,
        readOnly,
        title: 'Start',
        preview: '',
        promptTemplate: '',
        agentName: '',
        results: [],
        requiresReview: false,
        selected: selectedKey === startId,
        start: true,
        terminal: false,
        branchHandles: [OUTPUT_HANDLE_ID],
        sourceHandlePositions: startLayout.sourceHandles,
        targetHandlePositions: {},
        boxHeight: startLayout.height,
        badges: [],
        fields: inputFields,
        onSelect,
        agents,
        onExtendPath,
        onAddChoice,
        onToggleReview,
        onEditResult,
        onHoverTarget,
        onConnectPathTo: (source: string, target: string) =>
          onEditResult(connectPathTo(steps, source, target, agents)),
        joinTargets: [],
      },
      style: { width: startLayout.width, height: startLayout.height },
    },
    ...graph.nodes.flatMap((node: WorkflowGraphNode): CanvasFlowNode[] => {
      const box = layout.nodes[node.key];
      if (!box) return [];
      const agent = agents.find((candidate) => candidate.id === node.step.agentId);
      const outgoing = graph.connections.filter((transition) => transition.source === node.key);
      const handles = outgoing.length ? [OUTPUT_HANDLE_ID] : [];
      const resultNames = node.step.writes
        .filter((field) => field.name.trim() && !(field.label !== undefined && !field.label.trim()))
        .map((field) => {
          const kind =
            field.enumValues !== undefined
              ? 'pick-list'
              : field.type === 'boolean'
                ? 'yes/no'
                : field.type === 'string'
                  ? 'text'
                  : 'number';
          return `${fieldDisplayName(field)}${kind === 'text' ? '' : ` · ${kind}`}`;
        });
      const visibleResults = resultNames;
      return [
        {
          id: node.key,
          type: 'workflow-step',
          position: { x: box.x, y: box.y },
          width: box.width,
          height: box.height,
          data: {
            key: node.key,
            readOnly,
            title:
              overview &&
              node.step.agentId &&
              !agents.some((agent) => agent.id === node.step.agentId)
                ? `${node.displayName.split(' · ')[0]} · No one assigned`
                : node.displayName,
            preview: node.instructionPreview,
            promptTemplate: node.step.promptTemplate,
            agentName:
              overview &&
              node.step.agentId &&
              !agents.some((agent) => agent.id === node.step.agentId)
                ? 'No one assigned'
                : (agent?.name ??
                  (node.step.agentId ? (overview ? 'No one assigned' : 'Removed agent') : '')),
            agentIcon: agent?.icon,
            agentColor: agent?.color,
            results: visibleResults,
            requiresReview: node.step.requiresReview,
            selected: selectedKey === node.key,
            start: false,
            terminal: node.isTerminal,
            branchHandles: outgoing.length ? handles : [],
            sourceHandlePositions: box.sourceHandles,
            targetHandlePositions: box.targetHandles,
            boxHeight: box.height,
            badges: [
              ...(node.step.requiresReview ? ['Waits for your OK'] : []),
              ...stepProblemBadges(node.problems),
            ],
            problemMessage: node.problems
              .filter((problem) => problem.severity === 'error')
              .map((problem) => problem.message)
              .join(' '),
            fields: [...inputFields, ...steps.flatMap((candidate) => candidate.writes)],
            onSelect,
            agents,
            onExtendPath,
            onAddChoice,
            onToggleReview,
            onEditResult,
            onHoverTarget,
            onConnectPathTo: (source: string, target: string) =>
              onEditResult(connectPathTo(steps, source, target, agents)),
            joinTargets: node.isTerminal
              ? joinCandidates(steps, node.key).map((key) => ({
                  key,
                  name: graph.nodes.find((candidate) => candidate.key === key)?.displayName ?? key,
                }))
              : [],
          },
          style: { width: box.width, height: box.height },
        },
      ];
    }),
  ];
  const startEdgeId = startTarget ? `${startId}-${startTarget}` : '';
  const startEdgeLayout = startEdgeId ? layout.edges[startEdgeId] : undefined;
  const edges: Edge[] =
    startTarget && startEdgeLayout
      ? [
          {
            id: startEdgeId,
            source: startId,
            sourceHandle: 'out-main',
            target: startTarget,
            targetHandle: `target-${startId}-${startTarget}`,
            type: 'workflow-path',
            markerEnd: {
              type: MarkerType.ArrowClosed,
              color: 'hsl(var(--foreground) / 0.72)',
              width: 18,
              height: 18,
            },
            style: { stroke: 'hsl(var(--foreground) / 0.62)', strokeWidth: 2 },
            data: {
              readOnly: true,
              startEdge: true,
              label: '',
              kind: 'next',
              path: startEdgeLayout.path,
              labelWidth: 0,
              sourceStepKey: startId,
              targetStepKey: startTarget,
              addRef: { kind: 'next', stepKey: startTarget },
              addLabel: '',
              agents,
              onAddStep: () => undefined,
              onAddChoice: () => undefined,
              onConnectPathTo: () => undefined,
              joinTargets: [],
              onHoverTarget: () => undefined,
            },
            selectable: false,
          },
        ]
      : [];
  for (const [index, transition] of graph.connections.entries()) {
    const edgeId = `path-${index}-${transition.source}-${transition.target}`;
    const edgeLayout = layout.edges[edgeId];
    if (!edgeLayout) continue;
    const label = edgeLayout.labelHidden ? '' : stepEdgeLabel(steps, transition, inputFields);
    const addRef: ConnectionRef =
      transition.kind === 'next'
        ? { kind: 'next', stepKey: transition.source }
        : transition.kind === 'rule'
          ? { kind: 'rule', stepKey: transition.source, ruleKey: transition.ruleKey! }
          : { kind: 'otherwise', stepKey: transition.source };
    edges.push({
      id: edgeId,
      source: transition.source,
      sourceHandle: OUTPUT_HANDLE_ID,
      target: transition.target,
      targetHandle: `target-${edgeId}`,
      type: 'workflow-path',
      markerEnd: {
        type: MarkerType.ArrowClosed,
        color: 'hsl(var(--foreground) / 0.72)',
        width: 18,
        height: 18,
      },
      data: {
        readOnly,
        hideAddAction: edgeLayout.actionHidden,
        label,
        kind: transition.kind,
        path: edgeLayout.path,
        labelPosition: edgeLayout.labelPosition,
        labelWidth: edgeLayout.labelHidden ? 0 : edgeLayout.labelWidth,
        sourceStepKey: transition.source,
        targetStepKey: transition.target,
        addRef,
        addLabel: `Add a step after ${graph.nodes.find((node) => node.key === transition.source)?.displayName ?? 'this step'}`,
        agents,
        onAddStep,
        onAddChoice: onAddChoiceOnPath,
        onConnectPathTo: (source: string, target: string) =>
          onEditResult(connectPathTo(steps, source, target, agents)),
        joinTargets: [],
        onHoverTarget,
      },
      style: {
        stroke: 'hsl(var(--foreground) / 0.62)',
        strokeWidth: 2,
        ...(transition.kind === 'continue' ? { strokeDasharray: '7 5' } : {}),
      },
      selectable: false,
      interactionWidth: 1,
    });
  }
  return { nodes, edges };
}

export function WorkflowCanvas({
  steps,
  inputFields,
  agents,
  selectedKey,
  onSelect,
  name,
  onNameChange,
  onInputFieldsChange,
  onStepChange,
  onAddStep,
  onExtendPath,
  onDeleteStep,
  onEditResult,
  onAddChoiceRule,
  governed,
  showValidation,
  openStartWhenEmpty,
  onTestStep,
  readOnly: forceReadOnly = false,
}: {
  steps: WorkflowStepDraft[];
  inputFields: WorkflowFieldDraft[];
  agents: CanvasAgent[];
  selectedKey: string;
  onSelect: (key: string) => void;
  name: string;
  onNameChange: (name: string) => void;
  onInputFieldsChange: (fields: WorkflowFieldDraft[]) => void;
  onStepChange: (key: string, patch: Partial<WorkflowStepDraft>) => void;
  onAddStep: (ref: ConnectionRef, agentId: string) => void;
  onExtendPath: (key: string, agentId: string) => void;
  onDeleteStep: (steps: WorkflowStepDraft[]) => void;
  onEditResult: (result: EditResult) => void;
  onAddChoiceRule: (result: EditResult) => void;
  governed: boolean;
  showValidation: boolean;
  openStartWhenEmpty?: boolean;
  onTestStep: (
    stepKey: string,
    input: Record<string, unknown>
  ) => Promise<{
    results: Record<string, unknown> | null;
    error: string | null;
    droppedKeys: string[];
  }>;
  readOnly?: boolean;
}) {
  const returnFocus = useRef<HTMLElement | null>(null);
  const [hoveredKey, setHoveredKey] = useState<string | null>(null);
  const [isDesktop, setIsDesktop] = useState(
    () => typeof window === 'undefined' || window.matchMedia('(min-width: 1024px)').matches
  );
  const readOnly = forceReadOnly || !isDesktop;
  const showMobileNotice = !isDesktop && !forceReadOnly;
  const graph = useMemo(
    () => draftToGraph(steps, inputFields, agents, governed, showValidation),
    [steps, inputFields, agents, governed, showValidation]
  );
  const reserveAddActions = !readOnly;
  const layoutSignature = flowLayoutSignature(graph, steps, inputFields, reserveAddActions);
  const topologySignature = flowTopologySignature(graph, reserveAddActions, forceReadOnly);
  const layoutRequest = useRef({ graph, steps, inputFields });
  layoutRequest.current = { graph, steps, inputFields };
  const [layoutState, setLayoutState] = useState<WorkflowCanvasLayoutState | null>(null);
  const [layoutError, setLayoutError] = useState<string | null>(null);
  useEffect(() => {
    let cancelled = false;
    const request = layoutRequest.current;
    setLayoutError(null);
    void layoutWorkflow(request.graph, request.steps, request.inputFields, reserveAddActions)
      .then((layout) => {
        if (!cancelled) {
          setLayoutState({ signature: layoutSignature, topologySignature, layout });
          setLayoutError(null);
        }
      })
      .catch((error: unknown) => {
        if (!cancelled) {
          console.error('Could not lay out workflow', error);
          setLayoutError('We could not arrange these steps. Try reloading the page.');
        }
      });
    return () => {
      cancelled = true;
    };
  }, [layoutSignature, topologySignature]);
  const activeLayout =
    layoutState?.signature === layoutSignature ||
    layoutState?.topologySignature === topologySignature
      ? layoutState.layout
      : null;
  const renderedLayout = activeLayout ?? layoutState?.layout ?? null;
  const showWorkflowCanvas = Boolean(renderedLayout);
  const renderedSteps = steps;
  const renderedInputFields = inputFields;
  const { nodes, edges } = useMemo(() => {
    if (!renderedLayout) return { nodes: [], edges: [] };
    return makeFlow(
      renderedSteps,
      renderedInputFields,
      agents,
      selectedKey,
      (key) => {
        if (document.activeElement instanceof HTMLElement)
          returnFocus.current = document.activeElement;
        onSelect(key);
      },
      readOnly ? () => undefined : onAddStep,
      readOnly ? () => undefined : onExtendPath,
      (key) => {
        if (readOnly) return;
        const result = addChoice(steps, key, '', '');
        onEditResult(result);
        if (result.ok) onSelect(key);
      },
      (ref) => {
        if (readOnly) return;
        const inserted = addStepOn(steps, ref, '');
        if (!inserted.ok) {
          onEditResult(inserted);
          return;
        }
        const addedKey = inserted.steps.find(
          (candidate) => !steps.some((existing) => existing.key === candidate.key)
        )?.key;
        if (!addedKey) return;
        const split = addChoice(inserted.steps, addedKey, '', '');
        onEditResult(split);
        if (split.ok) onSelect(addedKey);
      },
      (key, value) => {
        if (!readOnly) onStepChange(key, { requiresReview: value });
      },
      readOnly ? () => undefined : onEditResult,
      setHoveredKey,
      governed,
      showValidation,
      readOnly,
      forceReadOnly,
      renderedLayout
    );
  }, [
    renderedSteps,
    renderedInputFields,
    agents,
    selectedKey,
    onSelect,
    onAddStep,
    onExtendPath,
    onEditResult,
    governed,
    showValidation,
    readOnly,
    forceReadOnly,
    renderedLayout,
  ]);
  const canvasRenderError =
    layoutError ||
    (renderedLayout && nodes.length === 0
      ? 'We could not arrange these steps. Try reloading the page.'
      : null);
  useEffect(() => {
    for (const node of document.querySelectorAll<HTMLElement>(
      '.workflow-canvas-editor .react-flow__node'
    )) {
      node
        .querySelector('.workflow-canvas-box')
        ?.classList.toggle('workflow-canvas-hover-target', node.dataset.id === hoveredKey);
    }
  }, [hoveredKey, nodes]);
  const selectedStep = steps.find((step) => step.key === selectedKey);
  const selectedStepProblems =
    graph.nodes
      .find((node) => node.key === selectedKey)
      ?.problems.filter((problem) => problem.severity === 'error') ?? [];
  useEffect(() => {
    if (openStartWhenEmpty && steps.length === 0 && !selectedKey) onSelect('__workflow_start__');
  }, [onSelect, openStartWhenEmpty, selectedKey, steps.length]);
  useEffect(() => {
    const media = window.matchMedia('(min-width: 1024px)');
    const update = () => setIsDesktop(media.matches);
    update();
    media.addEventListener('change', update);
    return () => media.removeEventListener('change', update);
  }, []);
  useEffect(() => {
    const onKeyboard = (event: KeyboardEvent) => {
      if (
        event.key === 'Escape' &&
        selectedKey &&
        event.target instanceof HTMLElement &&
        !event.target.closest('input, textarea, select, [contenteditable="true"], [role="dialog"]')
      ) {
        event.preventDefault();
        onSelect('');
        requestAnimationFrame(() => {
          const target =
            returnFocus.current ??
            document.querySelector<HTMLElement>(
              '.react-flow__node[data-id="__workflow_start__"] .workflow-canvas-box'
            );
          target?.focus();
        });
        return;
      }
      if (
        readOnly ||
        (event.key !== 'Delete' && event.key !== 'Backspace') ||
        !(event.target instanceof HTMLElement) ||
        !event.target.matches('.workflow-canvas-box-open')
      )
        return;
      const node = event.target.closest<HTMLElement>('.react-flow__node');
      const key = node?.dataset.id;
      if (!key || !steps.some((step) => step.key === key)) return;
      event.preventDefault();
      onSelect(key);
      requestAnimationFrame(() =>
        requestAnimationFrame(() =>
          document
            .querySelector<HTMLButtonElement>(
              '.workflow-canvas-settings button[aria-label="Delete step"]'
            )
            ?.click()
        )
      );
    };
    document.addEventListener('keydown', onKeyboard);
    return () => document.removeEventListener('keydown', onKeyboard);
  }, [onSelect, readOnly, selectedKey, steps]);
  return (
    <div
      className={`workflow-canvas-editor${forceReadOnly ? ' read-only' : ''}`}
      aria-label="Workflow steps and paths"
    >
      {showMobileNotice ? (
        <p className="workflow-canvas-mobile-notice text-sm text-muted-foreground">
          Edit this workflow on a larger screen.
        </p>
      ) : null}
      {canvasRenderError ? (
        <p role="alert" className="mb-2 text-sm text-destructive">
          {canvasRenderError}
        </p>
      ) : null}
      <div
        className="workflow-canvas-surface"
        aria-busy={activeLayout ? 'false' : 'true'}
        data-layout-ready={activeLayout ? 'true' : 'false'}
        data-layout-current={layoutState?.signature === layoutSignature ? 'true' : 'false'}
        data-node-count={nodes.length}
      >
        <ReactFlow
          nodes={nodes}
          edges={edges}
          fitView
          fitViewOptions={{
            padding: 0.12,
            minZoom: 0.65,
            maxZoom: 1,
            nodes: nodes.filter(
              (node) => node.id === '__workflow_start__' || node.id === steps[0]?.key
            ),
          }}
          minZoom={0.65}
          nodeTypes={nodeTypes}
          edgeTypes={edgeTypes}
          nodesDraggable={false}
          nodesConnectable={false}
          nodesFocusable={false}
          edgesFocusable={false}
          deleteKeyCode={null}
          selectionKeyCode={null}
          multiSelectionKeyCode={null}
          onPaneClick={() => onSelect('')}
          proOptions={{ hideAttribution: true }}
          style={{ visibility: renderedLayout ? 'visible' : 'hidden' }}
        >
          <Background color="hsl(var(--border))" gap={22} size={1} />
          <Controls showInteractive={false} showFitView={false} />
          <CanvasFitControl
            topologySignature={`${topologySignature}:${activeLayout ? 'layout-ready' : 'layout-pending'}`}
          />
          <CanvasSelectedNodeControl panelOpen={Boolean(selectedKey)} />
        </ReactFlow>
        {!showWorkflowCanvas && !canvasRenderError ? (
          <div
            role="status"
            className="absolute inset-0 z-30 flex items-center justify-center bg-background/80 text-sm text-muted-foreground backdrop-blur-[1px]"
          >
            Loading the workflow picture…
          </div>
        ) : null}
      </div>
      <aside className="workspace-panel workflow-canvas-settings space-y-4" hidden={!selectedKey}>
        <div className="flex justify-end">
          <Button
            type="button"
            variant="ghost"
            size="sm"
            aria-label={forceReadOnly ? 'Close' : 'Close settings'}
            onClick={() => onSelect('')}
          >
            <X className="h-4 w-4" />
          </Button>
        </div>
        {selectedKey &&
        !(showMobileNotice && selectedKey === '__workflow_start__' && steps.length === 0) ? (
          forceReadOnly && selectedStep ? (
            <ReadOnlyStepSummary
              step={selectedStep}
              steps={steps}
              inputFields={inputFields}
              agents={agents}
            />
          ) : forceReadOnly ? (
            <section aria-label="Start summary" className="space-y-3">
              <h2 className="text-base font-semibold">Start</h2>
              <p>
                {inputFields.length
                  ? inputFields.map(fieldDisplayName).join(', ')
                  : 'No start fields'}
              </p>
            </section>
          ) : (
            <fieldset
              disabled={readOnly}
              className="workflow-canvas-settings-fields min-w-0 space-y-4"
            >
              {selectedKey === '__workflow_start__' ? (
                <WorkflowStartSettings
                  name={name}
                  inputFields={inputFields}
                  agents={agents}
                  canAddFirstStep={steps.length === 0 && !readOnly}
                  onAddFirstStep={(agentId) =>
                    onEditResult({ ok: true, steps: [createStepDraft(agentId)] })
                  }
                  onNameChange={onNameChange}
                  onInputFieldsChange={onInputFieldsChange}
                />
              ) : selectedStep ? (
                <>
                  <h2 className="text-base font-semibold">
                    {graph.nodes.find((node) => node.key === selectedKey)?.displayName}
                  </h2>
                  {selectedStepProblems.map((problem) => (
                    <p
                      key={`${problem.code}:${problem.message}`}
                      className="rounded-md border border-destructive/40 bg-destructive/5 px-3 py-2 text-sm text-destructive"
                    >
                      {problem.message}
                    </p>
                  ))}
                  <WorkflowStepSettings
                    key={selectedStep.key}
                    step={selectedStep}
                    steps={steps}
                    inputFields={inputFields}
                    agents={agents}
                    governed={governed}
                    onSelect={onSelect}
                    onChange={(patch) => onStepChange(selectedStep.key, patch)}
                    onInputFieldsChange={onInputFieldsChange}
                    onDelete={onDeleteStep}
                    onEditResult={onEditResult}
                    onAddChoiceRule={onAddChoiceRule}
                    onTestStep={onTestStep}
                  />
                </>
              ) : null}
            </fieldset>
          )
        ) : null}
      </aside>
      <style>{`
        .workflow-canvas-editor { position: relative; width: 100%; }
        .workflow-canvas-node { position: relative; width: 100%; height: 100%; }
        .workflow-canvas-surface { box-sizing: border-box; width: 100%; height: min(680px, calc(100vh - 260px)); min-height: 480px; border: 1px solid hsl(var(--border)); border-radius: 1rem; overflow: hidden; padding: 12px; background: hsl(var(--background)); }
        .workflow-canvas-settings { position: absolute; z-index: 20; right: 12px; top: 12px; bottom: 12px; width: min(400px, calc(100% - 24px)); max-height: none; min-height: 0; overflow: auto; padding: 1rem; box-shadow: 0 8px 32px hsl(var(--foreground) / .18); }
        .workflow-canvas-settings-fields { border: 0; margin: 0; padding: 0; }
        .workflow-canvas-mobile-notice { display: none; }
        .workflow-canvas-box { position: relative; width: 100%; height: 100%; display: flex; flex-direction: column; align-items: flex-start; justify-content: flex-start; gap: 3px; overflow: hidden; padding: 8px 16px; border: 1px solid hsl(var(--border)); border-radius: 14px; background: hsl(var(--card)); color: hsl(var(--card-foreground)); text-align: left; box-shadow: 0 4px 14px hsl(var(--foreground) / .08); cursor: pointer; }
        .workflow-canvas-box:hover,.workflow-canvas-selected,.workflow-canvas-hover-target { border-color: hsl(var(--primary)); box-shadow: 0 0 0 3px hsl(var(--primary) / .15), 0 5px 16px hsl(var(--foreground) / .12); }
        .workflow-canvas-box strong { min-width: 0; max-width: 100%; overflow: visible; overflow-wrap: normal; white-space: normal; font-size: 18px; line-height: 20px; }
        .workflow-canvas-box-open,.workflow-canvas-start-open { width: 100%; min-width: 0; display: flex; flex-direction: column; align-items: flex-start; gap: 4px; padding: 0; border: 0; background: transparent; color: inherit; text-align: left; cursor: pointer; }
        .workflow-canvas-box-open > span:not(.workflow-canvas-preview):not(.workflow-canvas-results),.workflow-canvas-start-fields { font-size: 11px; line-height: 14px; color: hsl(var(--muted-foreground)); }
        .workflow-canvas-step-heading { display: flex; width: 100%; flex-wrap: wrap; align-items: center; gap: 4px 7px; overflow: visible; }
        .workflow-canvas-step-heading strong { display: -webkit-box; flex: 1 1 100%; overflow: hidden; overflow-wrap: normal; -webkit-box-orient: vertical; -webkit-line-clamp: 2; }
        .workflow-canvas-agent-icon { flex: 0 0 auto; font-size: 16px; line-height: 1; }
        .workflow-canvas-step-number { flex: 0 0 auto; border-radius: 999px; background: hsl(var(--muted)); color: hsl(var(--muted-foreground)); padding: 2px 7px; font-size: 18px; font-weight: 600; line-height: 20px; }
        .workflow-canvas-mention { border-radius: 4px; background: hsl(var(--primary) / .12); color: hsl(var(--primary)); padding: 0 2px; }
        .workflow-canvas-results { display: grid; max-width: 100%; gap: 3px; overflow: visible; }
        .workflow-canvas-results > span { width: max-content; max-width: 100%; overflow-wrap: anywhere; border-radius: 10px; background: hsl(var(--primary) / .1); color: hsl(var(--primary)); padding: 1px 6px; font-size: 16px; line-height: 18px; white-space: normal; }
        .workflow-canvas-preview { display: -webkit-box; max-width: 100%; max-height: 60px; overflow: hidden; overflow-wrap: normal; white-space: pre-wrap; -webkit-box-orient: vertical; -webkit-line-clamp: 3; font-size: 18px; line-height: 20px; color: hsl(var(--muted-foreground)); }
        .workflow-canvas-badges { display: flex; flex-direction: column; align-items: flex-start; max-width: 100%; gap: 1px; overflow: visible; }
        .workflow-canvas-badges span { max-width: 100%; overflow-wrap: normal; white-space: normal; color: hsl(var(--muted-foreground)); padding: 0 2px; font-size: 18px; line-height: 18px; }
        .workflow-canvas-badges span.problem { color: hsl(var(--destructive)); font-weight: 500; }
        .workflow-canvas-editor.read-only .workflow-canvas-box { gap: 0; padding: 6px 9px; overflow: visible; }
        .workflow-canvas-editor.read-only .workflow-canvas-box-open { gap: 1px; }
        .workflow-canvas-editor.read-only .workflow-canvas-start-fields { white-space: normal; overflow: visible; }
        .workflow-canvas-editor.read-only .workflow-canvas-step-heading { line-height: 20px; }
        .workflow-canvas-editor.read-only .workflow-canvas-step-heading strong { font-size: 18px; line-height: 20px; }
        .workflow-canvas-editor.read-only .workflow-canvas-preview { font-size: 16px; line-height: 18px; }
        .workflow-canvas-editor.read-only .workflow-canvas-results { overflow: visible; }
        .workflow-canvas-editor.read-only .workflow-canvas-results > span { flex: 0 1 auto; overflow-wrap: anywhere; white-space: normal; padding: 1px 6px; line-height: 18px; font-size: 16px; }
        .workflow-canvas-editor.read-only .workflow-canvas-badges { flex: 0 0 auto; max-width: 100%; }
        .workflow-canvas-editor.read-only .workflow-canvas-badges span { padding: 0 5px; line-height: 18px; font-size: 18px; }
        .workflow-canvas-start { justify-content: center; border-color: hsl(var(--border)); background: hsl(var(--muted)); }
        .workflow-canvas-start-fields { display: -webkit-box; max-width: 100%; max-height: 40px; overflow: hidden; overflow-wrap: anywhere; white-space: normal; -webkit-box-orient: vertical; -webkit-line-clamp: 2; font-size: 18px; line-height: 20px; color: hsl(var(--muted-foreground)); }
        .workflow-canvas-add-terminal { position: absolute; right: -14px; bottom: -16px; z-index: 10; }
        .workflow-canvas-edge-label { display: inline-flex; flex-direction: row; align-items: center; gap: 5px; max-width: 240px; padding: 3px 5px; border-radius: 8px; background: hsl(var(--popover)); color: hsl(var(--popover-foreground)); box-shadow: 0 1px 5px hsl(var(--foreground) / .12); font-size: 18px; line-height: 20px; }
        .workflow-canvas-edge-label:not(.read-only) > span:first-child { max-width: calc(100% - 31px); overflow-wrap: normal; word-break: normal; hyphens: none; white-space: normal; }
        .workflow-canvas-edge-label.read-only > span:first-child { max-width: 190px; overflow-wrap: normal; word-break: normal; hyphens: none; white-space: normal; }
        .workflow-canvas-edge-label.no-label { background: transparent; box-shadow: none; }
        .workflow-canvas-edge-label .workflow-canvas-add-action { opacity: 0; transition: opacity 120ms ease; }
        .workflow-canvas-edge-label:hover .workflow-canvas-add-action,.workflow-canvas-edge-label:focus-within .workflow-canvas-add-action { opacity: 1; }
        .workflow-canvas-add-action { position: relative; display: inline-flex; align-items: center; }
        .workflow-canvas-add-action > button { height: 26px; width: 26px; padding: 0; }
        .workflow-canvas-agent-picker { z-index: 100; display: grid; min-width: 220px; gap: 6px; border: 1px solid hsl(var(--border)); border-radius: 9px; background: hsl(var(--popover)); color: hsl(var(--popover-foreground)); padding: 8px; box-shadow: 0 6px 18px hsl(var(--foreground) / .14); }
        .workflow-canvas-agent-picker.open-up { top: auto; bottom: calc(100% + 5px); }
        .workflow-canvas-node .react-flow__handle { width: 9px; height: 9px; border: 0; background: transparent; opacity: 0; }
        .workflow-canvas-node .react-flow__handle-top { top: 0; }
        .workflow-canvas-node .react-flow__handle-bottom { bottom: 0; }
        .workflow-canvas-surface .react-flow__edge-textbg { fill: hsl(var(--popover)); }
        @media (max-width: 1023px) {
          .workflow-canvas-editor { display: block; }
          .workflow-canvas-editor .workflow-canvas-settings { position: relative; inset: auto; width: 100%; max-height: none; min-height: 0; margin-top: 1rem; }
          .workflow-canvas-mobile-notice { display: block; }
          .workflow-canvas-actions { display: none !important; }
          .workflow-canvas-editor .workflow-canvas-add-action { display: none !important; }
          .workflow-canvas-editor .react-flow__controls { display: none; }
        }
        @media (hover: none) {
          .workflow-canvas-edge-label .workflow-canvas-add-action { opacity: 1; pointer-events: auto; }
        }
      `}</style>
    </div>
  );
}

function CanvasFitControl({ topologySignature }: { topologySignature: string }) {
  const { fitView, getNodes } = useReactFlow();
  const previousTopology = useRef<string | null>(null);
  const fitWholeWorkflow = React.useCallback(async () => {
    const padding = 0.12;
    await fitView({ padding, minZoom: 0.65, maxZoom: 1 });
  }, [fitView]);
  const fitStartAndFirstStep = React.useCallback(async () => {
    const allNodes = getNodes();
    const firstStep = allNodes.find((node) => node.id !== '__workflow_start__');
    const startAndFirst = allNodes.filter(
      (node) => node.id === '__workflow_start__' || node.id === firstStep?.id
    );
    return fitView({ nodes: startAndFirst, padding: 0.12, minZoom: 0.65, maxZoom: 1 });
  }, [fitView, getNodes]);
  useEffect(() => {
    if (!topologySignature.endsWith(':layout-ready')) return;
    if (previousTopology.current === null) {
      previousTopology.current = topologySignature;
      return;
    }
    if (previousTopology.current === topologySignature) return;
    let nextFrame = 0;
    const frame = requestAnimationFrame(() => {
      nextFrame = requestAnimationFrame(() => {
        void fitWholeWorkflow();
        previousTopology.current = topologySignature;
      });
    });
    return () => {
      cancelAnimationFrame(frame);
      cancelAnimationFrame(nextFrame);
    };
  }, [fitWholeWorkflow, topologySignature]);
  useEffect(() => {
    let frame = 0;
    const refit = () => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => void fitStartAndFirstStep());
    };
    window.addEventListener('resize', refit);
    return () => {
      cancelAnimationFrame(frame);
      window.removeEventListener('resize', refit);
    };
  }, [fitStartAndFirstStep]);
  return (
    <div className="react-flow__panel top right">
      <Button type="button" variant="outline" size="sm" onClick={() => void fitWholeWorkflow()}>
        Fit
      </Button>
    </div>
  );
}

function CanvasSelectedNodeControl({ panelOpen }: { panelOpen: boolean }) {
  const { fitView } = useReactFlow();
  const wasPanelOpen = useRef(panelOpen);
  useEffect(() => {
    if (wasPanelOpen.current && !panelOpen) {
      void fitView({ padding: 0.2, minZoom: 0.65, maxZoom: 1 });
    }
    wasPanelOpen.current = panelOpen;
  }, [fitView, panelOpen]);
  return null;
}

function PickListAnswerPicker({
  label,
  value,
  answers,
  onChoose,
  onAddAnswer,
}: {
  label: string;
  value: string;
  answers: string[];
  onChoose: (answer: string) => void;
  onAddAnswer: (answer: string) => void;
}) {
  const [query, setQuery] = useState('');
  const [isFiltering, setIsFiltering] = useState(false);
  const [isOpen, setIsOpen] = useState(false);
  useEffect(() => {
    setQuery('');
    setIsFiltering(false);
    setIsOpen(false);
  }, [value]);
  const filteredAnswers =
    !isFiltering || !query.trim()
      ? answers
      : answers.filter((answer) => answer.toLowerCase().includes(query.trim().toLowerCase()));
  const canAdd =
    query.trim().length > 0 &&
    !answers.some((answer) => answer.toLowerCase() === query.trim().toLowerCase());
  const id = label.replace(/\W+/g, '-');

  return (
    <div className="space-y-1">
      <Label htmlFor={`${id}-input`}>is</Label>
      <Input
        id={`${id}-input`}
        role="combobox"
        aria-label={label}
        aria-expanded={isOpen}
        aria-controls={`${id}-options`}
        aria-autocomplete="list"
        value={isFiltering ? query : value}
        onFocus={(event) => {
          if (!isFiltering) event.currentTarget.select();
          setIsOpen(true);
        }}
        onClick={(event) => {
          if (!isFiltering) event.currentTarget.select();
        }}
        onBlur={() => {
          setQuery('');
          setIsFiltering(false);
          setIsOpen(false);
        }}
        onKeyDown={(event) => {
          if (event.key === 'Escape') {
            event.preventDefault();
            setQuery('');
            setIsFiltering(false);
            setIsOpen(false);
            event.currentTarget.blur();
          }
        }}
        onChange={(event) => {
          setIsFiltering(true);
          setQuery(event.target.value);
        }}
      />
      {isOpen ? (
        <div
          id={`${id}-options`}
          role="listbox"
          aria-label={`${label} options`}
          className="max-h-40 overflow-auto rounded-md border bg-background p-1"
        >
          {filteredAnswers.map((answer) => (
            <button
              key={answer}
              type="button"
              role="option"
              aria-selected={answer === value}
              className="block w-full rounded px-2 py-1 text-left text-sm hover:bg-muted"
              onMouseDown={(event) => event.preventDefault()}
              onClick={() => {
                onChoose(answer);
                setQuery('');
                setIsFiltering(false);
                setIsOpen(false);
              }}
            >
              {answer}
            </button>
          ))}
          {canAdd ? (
            <button
              type="button"
              role="option"
              aria-selected="false"
              className="block w-full rounded px-2 py-1 text-left text-sm text-primary hover:bg-muted"
              onMouseDown={(event) => event.preventDefault()}
              onClick={() => {
                onAddAnswer(query.trim());
                setQuery('');
                setIsFiltering(false);
                setIsOpen(false);
              }}
            >
              Add ‘{query.trim()}’ as an answer
            </button>
          ) : null}
          {filteredAnswers.length === 0 && !canAdd ? (
            <p className="px-2 py-1 text-sm text-muted-foreground">No matching answers</p>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}

export function WorkflowStepSettings({
  step,
  steps,
  inputFields,
  agents,
  governed = false,
  onSelect,
  onChange,
  onInputFieldsChange,
  onDelete,
  onEditResult,
  onAddChoiceRule,
  onTestStep,
}: {
  step: WorkflowStepDraft;
  steps: WorkflowStepDraft[];
  inputFields: WorkflowFieldDraft[];
  agents: CanvasAgent[];
  governed?: boolean;
  onSelect?: (key: string) => void;
  onChange: (patch: Partial<WorkflowStepDraft>) => void;
  onInputFieldsChange: (fields: WorkflowFieldDraft[]) => void;
  onDelete: (steps: WorkflowStepDraft[]) => void;
  onEditResult: (result: EditResult) => void;
  onAddChoiceRule?: (result: EditResult) => void;
  onTestStep?: (
    stepKey: string,
    input: Record<string, unknown>
  ) => Promise<{
    results: Record<string, unknown> | null;
    error: string | null;
    droppedKeys: string[];
  }>;
}) {
  const [deletePlan, setDeletePlan] = useState<DeletePlan | null>(null);
  const [addingStartField, setAddingStartField] = useState(false);
  const [newStartFieldName, setNewStartFieldName] = useState('');
  const [newStartFieldType, setNewStartFieldType] = useState<
    'text' | 'number' | 'yes/no' | 'pick-list'
  >('text');
  const [newStartFieldAnswers, setNewStartFieldAnswers] = useState('');
  const [newStartFieldError, setNewStartFieldError] = useState('');
  const [testOpen, setTestOpen] = useState(false);
  const [testValues, setTestValues] = useState<Record<string, string>>({});
  const [testResult, setTestResult] = useState<{
    results: Record<string, unknown> | null;
    error: string | null;
    droppedKeys: string[];
  } | null>(null);
  const [testPending, setTestPending] = useState(false);
  const testButtonRef = useRef<HTMLButtonElement>(null);
  const [deleteAction, setDeleteAction] = useState<
    { type: 'step' } | { type: 'rule'; ruleKey: string } | null
  >(null);
  const availableFields = guaranteedFields(steps, inputFields, step.key);
  const promptRef = useRef<HTMLTextAreaElement>(null);
  const promptSelection = useRef<[number, number]>([0, 0]);
  const pendingChoiceForResult = useRef<{ choiceKey: string; fieldKey: string } | null>(null);
  const mentionFields = [...inputFields, ...steps.flatMap((candidate) => candidate.writes)].filter(
    (field, index, all) =>
      availableFields.includes(field.name) &&
      !(field.label !== undefined && !field.label.trim()) &&
      all.findIndex((item) => item.name === field.name) === index
  );
  const graphNodes = draftToGraph(steps, inputFields, agents).nodes;
  const currentIndex = steps.findIndex((candidate) => candidate.key === step.key);
  const testInputFields = [
    ...inputFields,
    ...steps.slice(0, currentIndex).flatMap((source) =>
      source.handoffs.length
        ? source.handoffs.flatMap((handoff) => {
            const field = source.writes.find((candidate) => candidate.name === handoff.from);
            return field ? [{ ...field, name: handoff.to }] : [];
          })
        : source.writes
    ),
  ].filter(
    (field, index, all) =>
      availableFields.includes(field.name) &&
      all.findIndex((candidate) => candidate.name === field.name) === index
  );
  const resultSources = [
    { label: 'Input', stepKey: null as string | null, fields: inputFields },
    ...steps.slice(0, currentIndex + 1).map((source) => ({
      label: graphNodes.find((node) => node.key === source.key)?.displayName ?? 'Earlier step',
      stepKey: source.key,
      fields: source.handoffs.length
        ? source.handoffs.flatMap((handoff) => {
            const field = source.writes.find((candidate) => candidate.name === handoff.from);
            return field ? [{ ...field, name: handoff.to }] : [];
          })
        : source.writes,
    })),
  ];
  const currentStepResults = resultSources[resultSources.length - 1]?.fields ?? [];
  const conditionFields = new Set([
    ...availableFields,
    ...currentStepResults.map((field) => field.name),
  ]);
  const stepName = (key: string) => graphNodes.find((node) => node.key === key)?.displayName ?? key;
  const disconnectResult = disconnectPath(steps, step.key, agents);
  const insertMention = (key: string) => {
    const startField = inputFields.find((field) => field.name === key && field.newField);
    if (startField) {
      onInputFieldsChange(
        inputFields.map((field) =>
          field.key === startField.key ? { ...field, newField: false } : field
        )
      );
    } else {
      const referencedField = steps
        .flatMap((candidate) => candidate.writes)
        .find((field) => field.name === key && field.newField);
      if (referencedField) {
        onEditResult({
          ok: true,
          steps: steps.map((candidate) => ({
            ...candidate,
            writes: candidate.writes.map((field) =>
              field.key === referencedField.key ? { ...field, newField: false } : field
            ),
          })),
        });
      }
    }
    const text = step.promptTemplate;
    const [start, end] = promptSelection.current;
    const mention = `{{${key}}}`;
    const next = `${text.slice(0, start)}${mention}${text.slice(end)}`;
    onChange({ promptTemplate: next });
    const caret = start + mention.length;
    promptSelection.current = [caret, caret];
    requestAnimationFrame(() => {
      promptRef.current?.focus();
      promptRef.current?.setSelectionRange(caret, caret);
    });
  };
  const sourceForChoice = (choice: WorkflowStepDraft['choices'][number]) =>
    resultSources.find((source) => source.fields.some((field) => field.name === choice.field));
  const resultForChoice = (choice: WorkflowStepDraft['choices'][number]) =>
    sourceForChoice(choice)?.fields.find((field) => field.name === choice.field);
  const addPickListAnswerToSource = (
    choice: WorkflowStepDraft['choices'][number],
    answer: string
  ) => {
    const source = sourceForChoice(choice);
    const result = addPickListAnswer(
      steps,
      inputFields,
      source?.stepKey ?? null,
      choice.field,
      answer
    );
    if (!result.ok) {
      onEditResult(result);
      return;
    }
    onInputFieldsChange(result.inputFields);
    onEditResult({ ok: true, steps: result.steps });
    onChange({
      choices: step.choices.map((item) =>
        item.key === choice.key ? { ...item, op: 'eq', value: answer.trim() } : item
      ),
    });
  };
  const addResult = (choiceKey?: string) => {
    const latestStep = steps.find((candidate) => candidate.key === step.key) ?? step;
    const field = createFieldDraft();
    if (choiceKey) pendingChoiceForResult.current = { choiceKey, fieldKey: field.key };
    onChange(updateStepResults(latestStep, [...latestStep.writes, field]));
    requestAnimationFrame(() => {
      const inputs = document.querySelectorAll<HTMLInputElement>(
        '[aria-label="Results to pass on field name"]'
      );
      inputs.item(inputs.length - 1)?.focus();
    });
  };
  const addInlineStartField = () => {
    if (
      newStartFieldType === 'pick-list' &&
      !newStartFieldAnswers.split(',').some((answer) => answer.trim())
    ) {
      setNewStartFieldError('Add at least one answer.');
      return;
    }
    const type =
      newStartFieldType === 'yes/no'
        ? 'boolean'
        : newStartFieldType === 'text' || newStartFieldType === 'pick-list'
          ? 'string'
          : newStartFieldType;
    const next = addStartField(
      inputFields,
      newStartFieldName,
      newStartFieldType === 'pick-list' ? 'pick-list' : type
    );
    if (newStartFieldType === 'pick-list') {
      const last = next[next.length - 1]!;
      next[next.length - 1] = {
        ...last,
        enumValues: newStartFieldAnswers
          .split(',')
          .map((answer) => answer.trim())
          .filter(Boolean),
      };
    }
    onInputFieldsChange(next);
    setNewStartFieldName('');
    setNewStartFieldType('text');
    setNewStartFieldAnswers('');
    setNewStartFieldError('');
    setAddingStartField(false);
  };
  useEffect(() => {
    let changed = false;
    const choices = step.choices.map((choice) => {
      const result = resultForChoice(choice);
      if (result?.type !== 'boolean' || choice.op === 'exists') return choice;
      const value = choice.op === 'ne' || choice.value === 'false' ? 'false' : 'true';
      if (choice.op === 'eq' && choice.value === value) return choice;
      changed = true;
      return { ...choice, op: 'eq', value };
    });
    if (changed) onChange({ choices });
  }, [step.choices, step.writes, inputFields, steps, onChange]);
  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <Button
          type="button"
          variant="outline"
          size="sm"
          aria-label="Delete step"
          onClick={() => {
            setDeleteAction({ type: 'step' });
            setDeletePlan(planDeleteStep(steps, step.key, agents));
          }}
        >
          <Trash2 className="h-4 w-4" />
          Delete step
        </Button>
      </div>
      {deletePlan ? (
        <div
          role={deletePlan.ok ? 'alertdialog' : 'alert'}
          className="rounded-lg border border-border bg-muted/50 p-3 text-sm"
        >
          <p>{deletePlan.ok ? deletePlan.confirmation : deletePlan.reason}</p>
          {deletePlan.ok ? (
            <div className="mt-3 flex justify-end gap-2">
              <Button
                type="button"
                variant="outline"
                size="sm"
                onClick={() => {
                  setDeletePlan(null);
                  setDeleteAction(null);
                }}
              >
                Cancel
              </Button>
              <Button
                type="button"
                variant="destructive"
                size="sm"
                onClick={() => {
                  if (deleteAction?.type === 'step') {
                    const currentPlan = planDeleteStep(steps, step.key, agents);
                    if (currentPlan.ok) {
                      onDelete(currentPlan.steps);
                      setDeletePlan(null);
                      setDeleteAction(null);
                    } else setDeletePlan(currentPlan);
                  } else if (deleteAction?.type === 'rule') {
                    const currentPlan = planRemoveChoiceRule(
                      steps,
                      step.key,
                      deleteAction.ruleKey,
                      agents
                    );
                    if (currentPlan.ok) {
                      onEditResult({ ok: true, steps: currentPlan.steps });
                      setDeletePlan(null);
                      setDeleteAction(null);
                    } else setDeletePlan(currentPlan);
                  }
                }}
              >
                {deleteAction?.type === 'rule' ? 'Remove this check' : 'Delete'}
              </Button>
            </div>
          ) : (
            <div className="mt-3 flex justify-end">
              <Button
                type="button"
                variant="outline"
                size="sm"
                onClick={() => {
                  setDeletePlan(null);
                  setDeleteAction(null);
                }}
              >
                Close
              </Button>
            </div>
          )}
        </div>
      ) : null}
      <div className="space-y-1">
        <Label htmlFor={`canvas-agent-${step.key}`}>Who does this?</Label>
        {!agents.some((agent) => agent.id === step.agentId) && step.agentId ? (
          <p role="alert" className="text-sm text-destructive">
            This agent was removed. Choose another.
          </p>
        ) : null}
        <Select
          id={`canvas-agent-${step.key}`}
          value={step.agentId}
          onChange={(event) => onEditResult(replaceAgent(steps, step.key, event.target.value))}
        >
          <option value="">Choose who does this</option>
          {!agents.some((agent) => agent.id === step.agentId) && step.agentId ? (
            <option value={step.agentId}>This agent was removed. Choose another.</option>
          ) : null}
          {agents.map((agent) => (
            <option key={agent.id} value={agent.id}>
              {agent.name}
            </option>
          ))}
        </Select>
      </div>
      <div className="space-y-1">
        <Label htmlFor={`canvas-prompt-${step.key}`}>What should they do?</Label>
        <Textarea
          ref={promptRef}
          id={`canvas-prompt-${step.key}`}
          value={step.promptTemplate}
          onSelect={(event) => {
            promptSelection.current = [
              event.currentTarget.selectionStart,
              event.currentTarget.selectionEnd,
            ];
          }}
          onClick={(event) => {
            promptSelection.current = [
              event.currentTarget.selectionStart,
              event.currentTarget.selectionEnd,
            ];
          }}
          onChange={(event) => onChange({ promptTemplate: event.target.value })}
        />
        <div className="space-y-1">
          <p className="text-xs text-muted-foreground">This step can use:</p>
          <div className="flex flex-wrap gap-1">
            {mentionFields.map((field) => (
              <button
                key={field.name}
                type="button"
                className="rounded-full border bg-muted px-2 py-1 text-xs"
                onMouseDown={(event) => event.preventDefault()}
                onClick={() => insertMention(field.name)}
              >
                {fieldDisplayName(field)}
              </button>
            ))}
          </div>
          {currentIndex === 0 ? (
            <div className="space-y-2">
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={() => setAddingStartField((open) => !open)}
              >
                Add a start field
              </Button>
              {addingStartField ? (
                <div
                  role="group"
                  aria-label="New start field"
                  className="grid grid-cols-[minmax(0,1fr)_8rem_auto] gap-2"
                >
                  <Input
                    aria-label="New start field name"
                    placeholder="e.g. Customer name"
                    value={newStartFieldName}
                    onChange={(event) => setNewStartFieldName(event.target.value)}
                    onKeyDown={(event) => {
                      if (event.key === 'Enter') {
                        event.preventDefault();
                        addInlineStartField();
                      }
                    }}
                  />
                  <Select
                    aria-label="New start field type"
                    value={newStartFieldType}
                    onChange={(event) =>
                      setNewStartFieldType(event.target.value as typeof newStartFieldType)
                    }
                  >
                    <option value="text">Text</option>
                    <option value="number">Number</option>
                    <option value="yes/no">Yes or no</option>
                    <option value="pick-list">Pick from a list</option>
                  </Select>
                  {newStartFieldType === 'pick-list' ? (
                    <Input
                      className="col-span-2"
                      aria-label="New start field answers"
                      placeholder="Separate answers with commas"
                      value={newStartFieldAnswers}
                      onChange={(event) => setNewStartFieldAnswers(event.target.value)}
                    />
                  ) : null}
                  <Button
                    type="button"
                    size="sm"
                    disabled={!newStartFieldName.trim()}
                    onClick={addInlineStartField}
                  >
                    Add input
                  </Button>
                  {newStartFieldError ? (
                    <p role="alert" className="col-span-3 text-sm text-destructive">
                      {newStartFieldError}
                    </p>
                  ) : null}
                </div>
              ) : null}
            </div>
          ) : null}
          <p className="text-xs text-muted-foreground">
            {governed
              ? 'This step only sees what you mention here.'
              : 'This step already sees these. Click one to mention it by name.'}
          </p>
        </div>
      </div>
      <section className="space-y-2 rounded-lg border border-border p-3">
        <div className="flex items-center justify-between gap-2">
          <h3 className="text-sm font-semibold">Results to pass on</h3>
          <Button type="button" variant="outline" size="sm" onClick={() => addResult()}>
            <Plus className="h-4 w-4" /> Add a result
          </Button>
        </div>
        <EditableFields
          fields={step.writes}
          onChange={(writes) => {
            const pending = pendingChoiceForResult.current;
            const created = pending
              ? writes.find((field) => field.key === pending.fieldKey)
              : undefined;
            const resultPatch = updateStepResults(step, writes);
            if (pending && created) {
              onChange({
                ...resultPatch,
                choices: step.choices.map((choice) =>
                  choice.key === pending.choiceKey ? { ...choice, field: created.name } : choice
                ),
              });
              if (!created.newField) pendingChoiceForResult.current = null;
            } else {
              onChange(resultPatch);
            }
          }}
          label="Results to pass on"
          showRequired={false}
        />
      </section>
      {step.choices.length === 0 ? null : (
        <section
          className="space-y-3 rounded-lg border border-border p-3"
          aria-label="Choice paths"
        >
          <div className="flex items-center justify-between gap-2">
            <h3 className="text-sm font-semibold">Checks</h3>
            <Button
              type="button"
              size="sm"
              variant="outline"
              onClick={() => (onAddChoiceRule ?? onEditResult)(addChoiceRule(steps, step.key, ''))}
            >
              Add another check
            </Button>
          </div>
          <p className="text-xs text-muted-foreground">
            Kautuka tries these in order and follows the first one that’s true. If none are true, it
            goes to Otherwise.
          </p>
          {step.choices.map((choice, index) => (
            <div key={choice.key} className="space-y-2 rounded-md bg-muted/40 p-2">
              <div className="flex items-center justify-between">
                <strong className="rounded-md bg-background px-2 py-1 text-sm" aria-live="polite">
                  {formatChoiceSentence(choice, resultForChoice(choice))}
                </strong>
                <div className="flex gap-1">
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    aria-label={`Move If ${index + 1} up`}
                    disabled={index === 0}
                    onClick={() => onEditResult(moveRule(steps, step.key, choice.key, 'up'))}
                  >
                    ↑
                  </Button>
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    aria-label={`Move If ${index + 1} down`}
                    disabled={index === step.choices.length - 1}
                    onClick={() => onEditResult(moveRule(steps, step.key, choice.key, 'down'))}
                  >
                    ↓
                  </Button>
                </div>
              </div>
              <Select
                aria-label={`If ${index + 1} result`}
                value={choice.field}
                onChange={(event) =>
                  event.target.value === '__add_result__'
                    ? addResult(choice.key)
                    : onChange({
                        choices: step.choices.map((item) =>
                          item.key === choice.key
                            ? {
                                ...item,
                                field: event.target.value,
                                ...(resultSources
                                  .flatMap((source) => source.fields)
                                  .find((field) => field.name === event.target.value)?.type ===
                                'boolean'
                                  ? { op: 'eq', value: 'true' }
                                  : resultSources
                                        .flatMap((source) => source.fields)
                                        .find((field) => field.name === event.target.value) !==
                                      undefined
                                    ? { op: 'eq', value: '' }
                                    : {}),
                              }
                            : item
                        ),
                      })
                }
              >
                <option value="">Choose a result</option>
                {(() => {
                  const seenNames = new Set<string>();
                  return resultSources.map((source) => {
                    const fields = source.fields.filter((field) => {
                      if (
                        !field.name.trim() ||
                        !conditionFields.has(field.name) ||
                        seenNames.has(field.name)
                      )
                        return false;
                      seenNames.add(field.name);
                      return true;
                    });
                    return fields.length ? (
                      <optgroup key={source.label} label={source.label}>
                        {fields.map((field) => (
                          <option key={`${source.label}-${field.name}`} value={field.name}>
                            {fieldDisplayName(field)}
                          </option>
                        ))}
                      </optgroup>
                    ) : null;
                  });
                })()}
                <option value="__add_result__">Add a result to {stepName(step.key)}</option>
              </Select>
              {resultForChoice(choice)?.enumValues !== undefined ? (
                <PickListAnswerPicker
                  label={`If ${index + 1} answer`}
                  value={choice.value}
                  answers={resultForChoice(choice)?.enumValues ?? []}
                  onChoose={(answer) =>
                    onChange({
                      choices: step.choices.map((item) =>
                        item.key === choice.key ? { ...item, op: 'eq', value: answer } : item
                      ),
                    })
                  }
                  onAddAnswer={(answer) => addPickListAnswerToSource(choice, answer)}
                />
              ) : resultForChoice(choice)?.type === 'boolean' ? (
                <Select
                  aria-label={`If ${index + 1} comparison`}
                  value={
                    choice.op === 'exists'
                      ? 'exists'
                      : choice.value === 'false' || choice.op === 'ne'
                        ? 'false'
                        : 'true'
                  }
                  onChange={(event) =>
                    onChange({
                      choices: step.choices.map((item) =>
                        item.key === choice.key
                          ? {
                              ...item,
                              op: event.target.value === 'exists' ? 'exists' : 'eq',
                              value:
                                event.target.value === 'false'
                                  ? 'false'
                                  : event.target.value === 'true'
                                    ? 'true'
                                    : '',
                            }
                          : item
                      ),
                    })
                  }
                >
                  <option value="true">is Yes</option>
                  <option value="false">is No</option>
                  <option value="exists">has a value</option>
                </Select>
              ) : (
                <Select
                  aria-label={`If ${index + 1} comparison`}
                  value={choice.op}
                  onChange={(event) =>
                    onChange({
                      choices: step.choices.map((item) =>
                        item.key === choice.key
                          ? {
                              ...item,
                              op: event.target.value,
                              value: event.target.value === 'exists' ? '' : item.value,
                            }
                          : item
                      ),
                    })
                  }
                >
                  {workflowChoiceOps
                    .filter((op) => {
                      const type = resultForChoice(choice)?.type;
                      const isNumber = type === 'number' || type === 'integer';
                      if (isNumber)
                        return ['gt', 'lt', 'eq', 'gte', 'lte', 'exists'].includes(op.id);
                      return ['eq', 'ne', 'in', 'exists'].includes(op.id);
                    })
                    .sort((left, right) => {
                      const type = resultForChoice(choice)?.type;
                      if (type !== 'number' && type !== 'integer') return 0;
                      const order = ['gt', 'lt', 'eq', 'gte', 'lte', 'exists'];
                      return order.indexOf(left.id) - order.indexOf(right.id);
                    })
                    .map((op) => (
                      <option key={op.id} value={op.id}>
                        {op.id === 'exists'
                          ? 'has a value'
                          : op.id === 'gt'
                            ? 'is more than'
                            : op.id === 'lt'
                              ? 'is less than'
                              : op.id === 'gte'
                                ? 'is at least'
                                : op.id === 'lte'
                                  ? 'is at most'
                                  : op.label}
                      </option>
                    ))}
                </Select>
              )}
              {choice.op !== 'exists' &&
              resultForChoice(choice)?.enumValues === undefined &&
              resultForChoice(choice)?.type !== 'boolean' ? (
                <Input
                  type={
                    resultForChoice(choice)?.type === 'number' ||
                    resultForChoice(choice)?.type === 'integer'
                      ? 'number'
                      : 'text'
                  }
                  inputMode={
                    resultForChoice(choice)?.type === 'number' ||
                    resultForChoice(choice)?.type === 'integer'
                      ? 'decimal'
                      : undefined
                  }
                  aria-label={`If ${index + 1} value`}
                  value={choice.value}
                  onChange={(event) =>
                    onChange({
                      choices: step.choices.map((item) =>
                        item.key === choice.key ? { ...item, value: event.target.value } : item
                      ),
                    })
                  }
                  onBlur={(event) => {
                    const value = event.target.value;
                    if (
                      (resultForChoice(choice)?.type === 'number' ||
                        resultForChoice(choice)?.type === 'integer') &&
                      value &&
                      Number.isFinite(Number(value))
                    ) {
                      const normalized = String(Number(value));
                      if (normalized !== value)
                        onChange({
                          choices: step.choices.map((item) =>
                            item.key === choice.key ? { ...item, value: normalized } : item
                          ),
                        });
                    }
                  }}
                  placeholder="Enter a value"
                />
              ) : null}
              {choice.op === 'in' ? (
                <p className="text-xs text-muted-foreground">Separate values with commas.</p>
              ) : null}
              {(() => {
                const destination = effectiveTransitions(steps).find(
                  (transition) =>
                    transition.source === step.key &&
                    transition.kind === 'rule' &&
                    transition.ruleKey === choice.key
                )?.target;
                const destinationNode = graphNodes.find((node) => node.key === destination);
                return destination ? (
                  <p className="text-sm">
                    {formatChoiceSentence(choice, resultForChoice(choice))} →{' '}
                    <button
                      type="button"
                      className="font-semibold text-primary underline-offset-2 hover:underline"
                      onClick={() => onSelect?.(destination)}
                    >
                      {stepName(destination)}
                      {destinationNode?.instructionPreview
                        ? ` (${destinationNode.instructionPreview})`
                        : ''}
                    </button>
                  </p>
                ) : null;
              })()}
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={() => {
                  setDeleteAction({ type: 'rule', ruleKey: choice.key });
                  setDeletePlan(planRemoveChoiceRule(steps, step.key, choice.key, agents));
                }}
              >
                Remove this check
              </Button>
            </div>
          ))}
          {(() => {
            const destination = effectiveTransitions(steps).find(
              (transition) => transition.source === step.key && transition.kind === 'otherwise'
            )?.target;
            const destinationNode = graphNodes.find((node) => node.key === destination);
            return destination ? (
              <p className="text-sm">
                Otherwise →{' '}
                <button
                  type="button"
                  className="font-semibold text-primary underline-offset-2 hover:underline"
                  onClick={() => onSelect?.(destination)}
                >
                  {stepName(destination)}
                  {destinationNode?.instructionPreview
                    ? ` (${destinationNode.instructionPreview})`
                    : ''}
                </button>
              </p>
            ) : null;
          })()}
        </section>
      )}
      {step.choices.length === 0 &&
      effectiveTransitions(steps).some(
        (transition) => transition.source === step.key && transition.kind === 'continue'
      ) ? (
        <section
          className="space-y-2 rounded-lg border border-border p-3"
          aria-label="Continuation"
        >
          {(() => {
            const destination = effectiveTransitions(steps).find(
              (transition) => transition.source === step.key && transition.kind === 'continue'
            )?.target;
            const destinationNode = graphNodes.find((node) => node.key === destination);
            return destination ? (
              <p className="text-sm">
                Then{' '}
                <button
                  type="button"
                  className="font-semibold text-primary underline-offset-2 hover:underline"
                  onClick={() => onSelect?.(destination)}
                >
                  {stepName(destination)}
                  {destinationNode?.instructionPreview
                    ? ` (${destinationNode.instructionPreview})`
                    : ''}
                </button>
              </p>
            ) : null;
          })()}
          {disconnectResult.ok ? (
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => onEditResult(disconnectResult)}
            >
              Stop here instead
            </Button>
          ) : (
            <p className="text-sm text-muted-foreground">{disconnectResult.reason}</p>
          )}
        </section>
      ) : null}
      <label className="flex items-center gap-2 text-sm">
        <Checkbox
          checked={step.requiresReview}
          onChange={(event) => onChange({ requiresReview: event.target.checked })}
        />
        Check with me first
      </label>
      {onTestStep ? (
        <Button
          ref={testButtonRef}
          type="button"
          variant="outline"
          size="sm"
          onClick={() => {
            setTestValues({});
            setTestResult(null);
            setTestOpen(true);
          }}
        >
          Test this step
        </Button>
      ) : null}
      <Dialog open={testOpen} onOpenChange={setTestOpen}>
        <DialogContent
          onCloseAutoFocus={(event) => {
            event.preventDefault();
            testButtonRef.current?.focus();
          }}
        >
          <DialogHeader>
            <DialogTitle>Test this step</DialogTitle>
          </DialogHeader>
          <div className="space-y-3">
            {testInputFields.map((field) => (
              <div key={field.name} className="space-y-1">
                <Label htmlFor={`test-${step.key}-${field.name}`}>
                  {fieldDisplayName(field)}
                  {inputFields.some((input) => input.name === field.name) && field.required
                    ? ' (required)'
                    : ''}
                </Label>
                {field.enumValues !== undefined ? (
                  <select
                    id={`test-${step.key}-${field.name}`}
                    className="h-10 w-full rounded-md border border-input bg-background px-3 text-sm"
                    value={testValues[field.name] ?? ''}
                    onChange={(event) =>
                      setTestValues((values) => ({ ...values, [field.name]: event.target.value }))
                    }
                    required={
                      inputFields.some((input) => input.name === field.name) && field.required
                    }
                    aria-required={
                      inputFields.some((input) => input.name === field.name) && field.required
                    }
                  >
                    <option value="">
                      {field.enumValues.length ? 'Choose an answer' : 'No answers yet'}
                    </option>
                    {field.enumValues.map((answer) => (
                      <option key={answer} value={answer}>
                        {answer}
                      </option>
                    ))}
                  </select>
                ) : field.type === 'boolean' ? (
                  <select
                    id={`test-${step.key}-${field.name}`}
                    className="h-10 w-full rounded-md border border-input bg-background px-3 text-sm"
                    value={testValues[field.name] ?? ''}
                    onChange={(event) =>
                      setTestValues((values) => ({ ...values, [field.name]: event.target.value }))
                    }
                    required={
                      inputFields.some((input) => input.name === field.name) && field.required
                    }
                    aria-required={
                      inputFields.some((input) => input.name === field.name) && field.required
                    }
                  >
                    <option value="">Choose yes or no</option>
                    <option value="true">Yes</option>
                    <option value="false">No</option>
                  </select>
                ) : (
                  <Input
                    id={`test-${step.key}-${field.name}`}
                    type={field.type === 'number' || field.type === 'integer' ? 'number' : 'text'}
                    step={field.type === 'integer' ? '1' : undefined}
                    value={testValues[field.name] ?? ''}
                    onChange={(event) =>
                      setTestValues((values) => ({ ...values, [field.name]: event.target.value }))
                    }
                    required={
                      inputFields.some((input) => input.name === field.name) && field.required
                    }
                    aria-required={
                      inputFields.some((input) => input.name === field.name) && field.required
                    }
                  />
                )}
              </div>
            ))}
            <Button
              type="button"
              disabled={testPending || !onTestStep}
              onClick={() => {
                if (!onTestStep) return;
                const missingRequired = inputFields.find(
                  (field) =>
                    field.required &&
                    availableFields.includes(field.name) &&
                    !(testValues[field.name] ?? '').trim()
                );
                if (missingRequired) {
                  setTestResult({
                    results: null,
                    error: `Fill in ${fieldDisplayName(missingRequired)} first.`,
                    droppedKeys: [],
                  });
                  return;
                }
                setTestPending(true);
                void onTestStep(
                  step.key,
                  Object.fromEntries(
                    Object.entries(testValues).filter(([, value]) => value.trim() !== '')
                  )
                )
                  .then(setTestResult)
                  .finally(() => setTestPending(false));
              }}
            >
              {testPending ? 'Testing…' : 'Test step'}
            </Button>
            {testResult?.error ? (
              <p role="alert" className="text-sm text-destructive">
                {testResult.error}
              </p>
            ) : null}
            {testResult?.results ? (
              <div aria-label="Step test results" className="space-y-1">
                {Object.entries(testResult.results).map(([key, value]) => (
                  <p key={key} className="text-sm">
                    {fieldDisplayName(
                      step.writes.find((field) => field.name === key) ?? { name: key }
                    )}
                    :{' '}
                    {typeof value === 'boolean'
                      ? value
                        ? 'Yes'
                        : 'No'
                      : typeof value === 'string'
                        ? value
                        : JSON.stringify(value)}
                  </p>
                ))}
              </div>
            ) : null}
          </div>
        </DialogContent>
      </Dialog>
      <details className="rounded-lg border border-border p-3">
        <summary className="cursor-pointer text-sm font-semibold">Advanced</summary>
        <div className="mt-3 space-y-4">
          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={() => onChange({ handoffs: automaticHandoffs(step.writes) })}
          >
            Go back to automatic
          </Button>
          <EditableHandoffs step={step} onChange={onChange} />
        </div>
      </details>
    </div>
  );
}

export function WorkflowStartSettings({
  name,
  inputFields,
  agents,
  canAddFirstStep,
  onAddFirstStep,
  onNameChange,
  onInputFieldsChange,
}: {
  name: string;
  inputFields: WorkflowFieldDraft[];
  agents: CanvasAgent[];
  canAddFirstStep: boolean;
  onAddFirstStep: (agentId: string) => void;
  onNameChange: (name: string) => void;
  onInputFieldsChange: (fields: WorkflowFieldDraft[]) => void;
}) {
  const [firstAgentId, setFirstAgentId] = useState('');
  const [newFieldName, setNewFieldName] = useState('');
  const [newFieldType, setNewFieldType] = useState<'text' | 'number' | 'yes/no' | 'pick-list'>(
    'text'
  );
  const [newFieldAnswers, setNewFieldAnswers] = useState('');
  const [newFieldError, setNewFieldError] = useState('');
  const addInput = () => {
    if (!newFieldName.trim()) return;
    const answers = newFieldAnswers
      .split(',')
      .map((answer) => answer.trim())
      .filter(Boolean);
    if (newFieldType === 'pick-list' && answers.length === 0) {
      setNewFieldError('Add at least one answer.');
      return;
    }
    const type =
      newFieldType === 'yes/no' ? 'boolean' : newFieldType === 'number' ? 'number' : 'string';
    const next = addStartField(
      inputFields,
      newFieldName,
      newFieldType === 'pick-list' ? 'pick-list' : type
    );
    if (newFieldType === 'pick-list') {
      const last = next[next.length - 1]!;
      next[next.length - 1] = { ...last, enumValues: answers };
    }
    onInputFieldsChange(next);
    setNewFieldName('');
    setNewFieldAnswers('');
    setNewFieldType('text');
    setNewFieldError('');
  };
  return (
    <div className="space-y-4">
      <div className="space-y-1">
        <Label htmlFor="canvas-workflow-name">Workflow name</Label>
        <Input
          id="canvas-workflow-name"
          value={name}
          onChange={(event) => onNameChange(event.target.value)}
        />
      </div>
      <div className="space-y-2">
        <div className="flex items-center justify-between">
          <h3 className="text-sm font-semibold">Input</h3>
        </div>
        <div className="grid grid-cols-[minmax(0,1fr)_8rem_auto] gap-2">
          <Input
            aria-label="New start field name"
            placeholder="e.g. Customer name"
            value={newFieldName}
            onChange={(event) => setNewFieldName(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter') {
                event.preventDefault();
                addInput();
              }
            }}
          />
          <Select
            aria-label="New start field type"
            value={newFieldType}
            onChange={(event) => {
              setNewFieldType(event.target.value as typeof newFieldType);
              setNewFieldError('');
            }}
          >
            <option value="text">Text</option>
            <option value="number">Number</option>
            <option value="yes/no">Yes or no</option>
            <option value="pick-list">Pick from a list</option>
          </Select>
          <Button type="button" disabled={!newFieldName.trim()} onClick={addInput}>
            Add
          </Button>
          {newFieldType === 'pick-list' ? (
            <Input
              className="col-span-3"
              aria-label="New start field answers"
              placeholder="Separate answers with commas"
              value={newFieldAnswers}
              onChange={(event) => {
                setNewFieldAnswers(event.target.value);
                if (event.target.value.split(',').some((answer) => answer.trim()))
                  setNewFieldError('');
              }}
            />
          ) : null}
          {newFieldError ? (
            <p role="alert" className="col-span-3 text-sm text-destructive">
              {newFieldError}
            </p>
          ) : null}
        </div>
        <EditableFields
          fields={inputFields}
          onChange={onInputFieldsChange}
          label="Start fields"
          emptyHint="Optional. Add fields if each run needs details from you."
        />
      </div>
      {canAddFirstStep ? (
        <div className="space-y-2 rounded-lg border border-border p-3">
          <Label htmlFor="workflow-first-agent">Who does the first step?</Label>
          <Select
            id="workflow-first-agent"
            value={firstAgentId}
            onChange={(event) => setFirstAgentId(event.target.value)}
          >
            <option value="">Choose an agent</option>
            {agents.map((agent) => (
              <option key={agent.id} value={agent.id}>
                {agent.name}
              </option>
            ))}
          </Select>
          <Button
            type="button"
            disabled={!firstAgentId}
            onClick={() => onAddFirstStep(firstAgentId)}
          >
            Add first step
          </Button>
        </div>
      ) : null}
    </div>
  );
}

function EditableFields({
  fields,
  onChange,
  label,
  showRequired = true,
  emptyHint,
}: {
  fields: WorkflowFieldDraft[];
  onChange: (fields: WorkflowFieldDraft[]) => void;
  label: string;
  showRequired?: boolean;
  emptyHint?: string;
}) {
  const [answerDrafts, setAnswerDrafts] = useState<Record<string, string>>({});
  return (
    <div className="space-y-2" aria-label={label}>
      {fields.map((field) => (
        <div
          key={field.key}
          className="grid grid-cols-[minmax(0,1fr)_7rem_auto_auto] items-center gap-2"
        >
          <Input
            aria-label={`${label} field name`}
            value={field.label ?? fieldDisplayName(field)}
            placeholder="e.g. Customer name"
            onChange={(event) =>
              onChange(
                fields.map((item) =>
                  item.key === field.key
                    ? {
                        ...item,
                        label: event.target.value,
                        hasTitle: true,
                        name: item.newField
                          ? event.target.value.trim()
                            ? fieldKeyFromLabel(event.target.value, fields, item.key)
                            : ''
                          : item.name,
                      }
                    : item
                )
              )
            }
            onBlur={() => {
              if (!field.newField) return;
              onChange(
                fields.map((item) => (item.key === field.key ? { ...item, newField: false } : item))
              );
            }}
          />
          <Select
            aria-label={`${label} field type`}
            disabled={field.isReadOnlyType}
            value={
              field.isArray
                ? 'list'
                : field.enumValues !== undefined
                  ? 'pick-list'
                  : field.type === 'boolean'
                    ? 'yes/no'
                    : field.type === 'number' || field.type === 'integer'
                      ? 'number'
                      : 'text'
            }
            onChange={(event) => {
              setAnswerDrafts((current) => {
                const next = { ...current };
                delete next[field.key];
                return next;
              });
              onChange(
                fields.map((item) =>
                  item.key === field.key
                    ? {
                        ...item,
                        type:
                          event.target.value === 'yes/no'
                            ? 'boolean'
                            : event.target.value === 'number'
                              ? 'number'
                              : 'string',
                        enumValues:
                          event.target.value === 'pick-list' ? (item.enumValues ?? []) : undefined,
                      }
                    : item
                )
              );
            }}
          >
            {field.isArray ? <option value="list">List</option> : null}
            <option value="number">Number</option>
            <option value="text">Text</option>
            <option value="yes/no">Yes or no</option>
            <option value="pick-list">Pick from a list</option>
          </Select>
          {field.enumValues ? (
            <div className="col-span-full row-start-2">
              <Input
                className="w-full"
                aria-label={`${label} answers`}
                placeholder="Separate answers with commas"
                value={answerDrafts[field.key] ?? field.enumValues.join(', ')}
                onChange={(event) => {
                  const rawAnswers = event.target.value;
                  setAnswerDrafts((current) => ({ ...current, [field.key]: rawAnswers }));
                  onChange(
                    fields.map((item) =>
                      item.key === field.key
                        ? {
                            ...item,
                            enumValues: rawAnswers
                              .split(',')
                              .map((value) => value.trim())
                              .filter(Boolean),
                          }
                        : item
                    )
                  );
                }}
                onBlur={() => {
                  const rawAnswers = answerDrafts[field.key];
                  if (rawAnswers === undefined) return;
                  onChange(
                    fields.map((item) =>
                      item.key === field.key
                        ? {
                            ...item,
                            enumValues: rawAnswers
                              .split(',')
                              .map((value) => value.trim())
                              .filter(Boolean),
                          }
                        : item
                    )
                  );
                  setAnswerDrafts((current) => {
                    const next = { ...current };
                    delete next[field.key];
                    return next;
                  });
                }}
                onKeyDown={(event) => {
                  if (event.key === 'Enter') {
                    event.preventDefault();
                    event.currentTarget.blur();
                  }
                }}
              />
              {field.enumValues.length === 0 ? (
                <p className="text-sm text-destructive">Add at least one answer</p>
              ) : null}
            </div>
          ) : null}
          {showRequired ? (
            <label className="col-start-3 row-start-1 flex items-center gap-1 text-xs">
              <Checkbox
                checked={field.required}
                onChange={(event) =>
                  onChange(
                    fields.map((item) =>
                      item.key === field.key ? { ...item, required: event.target.checked } : item
                    )
                  )
                }
              />
              Required
            </label>
          ) : null}
          <Button
            type="button"
            className="col-start-4 row-start-1"
            variant="ghost"
            size="sm"
            aria-label={`Remove ${label} field ${field.name || 'untitled'}`}
            disabled={fields.length <= 1}
            onClick={() => onChange(fields.filter((item) => item.key !== field.key))}
          >
            <Trash2 className="h-4 w-4" />
          </Button>
        </div>
      ))}
      {!fields.length && emptyHint ? (
        <p className="text-sm text-muted-foreground">{emptyHint}</p>
      ) : null}
    </div>
  );
}

function EditableHandoffs({
  step,
  onChange,
}: {
  step: WorkflowStepDraft;
  onChange: (patch: Partial<WorkflowStepDraft>) => void;
}) {
  return (
    <div className="space-y-2">
      <div className="flex items-center justify-between">
        <h3 className="text-sm font-semibold">Pass results on</h3>
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() =>
            onChange({
              handoffs: [...step.handoffs, createHandoffDraft(step.writes[0]?.name ?? '', '')],
            })
          }
        >
          <Plus className="h-4 w-4" />
          Add mapping
        </Button>
      </div>
      {step.handoffs.map((handoff) => (
        <div key={handoff.key} className="grid grid-cols-[1fr_auto_1fr_auto] items-center gap-2">
          <Select
            aria-label="Result to pass on"
            value={handoff.from}
            onChange={(event) =>
              onChange({
                handoffs: step.handoffs.map((item) =>
                  item.key === handoff.key ? { ...item, from: event.target.value } : item
                ),
              })
            }
          >
            <option value="">Choose a result</option>
            {step.writes
              .filter((field) => field.name.trim())
              .map((field) => (
                <option key={field.key} value={field.name}>
                  {fieldDisplayName(field)}
                </option>
              ))}
          </Select>
          <span className="text-xs text-muted-foreground">to</span>
          <Input
            aria-label="Name it for the next step"
            value={handoff.to}
            onChange={(event) =>
              onChange({
                handoffs: step.handoffs.map((item) =>
                  item.key === handoff.key ? { ...item, to: event.target.value } : item
                ),
              })
            }
          />
          <Button
            type="button"
            variant="ghost"
            size="sm"
            aria-label="Remove result mapping"
            disabled={step.handoffs.length <= 1}
            onClick={() =>
              onChange({ handoffs: step.handoffs.filter((item) => item.key !== handoff.key) })
            }
          >
            <Trash2 className="h-4 w-4" />
          </Button>
        </div>
      ))}
    </div>
  );
}

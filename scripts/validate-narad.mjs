import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { createRequire } from 'node:module';
import { isDeepStrictEqual } from 'node:util';
import { fileURLToPath } from 'node:url';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(import.meta.url);
const naradRoot = path.dirname(require.resolve('@devkrity/narad-spec/package.json'));
const schemaRoot = path.join(naradRoot, 'schema');
const traceRoot = path.join(naradRoot, 'conformance');
const failures = [];

if (!fs.existsSync(schemaRoot) || !fs.existsSync(traceRoot)) {
  failures.push(
    `${relative(naradRoot)} is missing schema/ or conformance/. pnpm 11.5.2 drops path: from gitHosted lock resolutions; restore path: packages/narad-spec on the root lockfile.`,
  );
}

const schemaFiles = [
  'narad-core-event.schema.json',
  'narad-control.schema.json',
  'narad-hydrate.schema.json',
  'narad-profile-event.schema.json',
];
const schemas = new Map();

for (const name of schemaFiles) {
  const filePath = path.join(schemaRoot, name);
  try {
    const schema = JSON.parse(fs.readFileSync(filePath, 'utf8'));
    if (schema.$schema !== 'https://json-schema.org/draft/2020-12/schema') {
      failures.push(`${relative(filePath)}: expected JSON Schema 2020-12.`);
    }
    if (!schema.$id || !schema.oneOf) {
      failures.push(`${relative(filePath)}: schema must define $id and oneOf.`);
    }
    schemas.set(name, schema);
  } catch (error) {
    failures.push(`${relative(filePath)}: ${error.message}`);
  }
}

const ajv = new Ajv2020({ allErrors: true, strict: false });
addFormats(ajv);
const validators = new Map();
for (const [name, schema] of schemas) {
  try {
    validators.set(name, ajv.compile(schema));
  } catch (error) {
    failures.push(`${name}: schema compilation failed: ${error.message}`);
  }
}

const controlTypes = new Set([
  'client.attached',
  'server.heartbeat',
  'client.heartbeat',
  'client.tool.execute',
  'client.tool.finish',
]);
const hydrateTypes = new Set(['messages.snapshot', 'graph.snapshot', 'graph.delta']);
const coreTypes = new Set([
  'run.started',
  'run.paused',
  'run.resumed',
  'run.stop.requested',
  'run.finished',
  'run.error',
  'run.cancelled',
  'interrupt.requested',
  'interrupt.resolved',
  'tool.call.started',
  'tool.call.finished',
  'session.revision',
  'run.revision',
]);
const workspaceWatchTypes = new Set([
  'workspace.revision',
  'session.created',
  'session.updated',
  'session.archived',
]);
const watchTypes = new Set([
  'session.revision',
  'run.revision',
  'workflow.revision',
  'node.revision',
  ...workspaceWatchTypes,
]);
const terminalTypes = new Set(['run.finished', 'run.error', 'run.cancelled']);
const runTreeTypes = new Set([
  'run.started',
  'run.paused',
  'run.resumed',
  'run.stop.requested',
  'run.finished',
  'run.error',
  'run.cancelled',
  'interrupt.requested',
  'interrupt.resolved',
  'tool.call.started',
  'tool.call.finished',
  'progress.recorded',
]);

const traceFiles = fs.existsSync(traceRoot)
  ? fs.readdirSync(traceRoot)
    .filter(name => name.endsWith('.jsonl') && !name.endsWith('.invalid.jsonl'))
    .sort()
  : [];
const invalidTraceFiles = fs.existsSync(traceRoot)
  ? fs.readdirSync(traceRoot)
    .filter(name => name.endsWith('.invalid.jsonl'))
    .sort()
  : [];

if (traceFiles.length < 4) failures.push('Narad conformance requires at least four golden JSONL traces.');

for (const name of traceFiles) {
  const traceFailures = validateTrace(path.join(traceRoot, name));
  failures.push(...traceFailures);
}

for (const name of invalidTraceFiles) {
  const traceFailures = validateTrace(path.join(traceRoot, name));
  if (traceFailures.length === 0) {
    failures.push(`${relative(path.join(traceRoot, name))}: expected semantic validation failure but trace passed.`);
  }
}

if (failures.length > 0) {
  console.error('Narad validation failed:');
  for (const failure of failures) console.error(`- ${failure}`);
  process.exit(1);
}

console.log(`Narad validation passed (${traceFiles.length} golden traces, ${invalidTraceFiles.length} negative traces, ${schemaFiles.length} schemas).`);

function validateTrace(filePath) {
  const traceFailures = [];
  const fail = (at, message) => {
    traceFailures.push(`${at}: ${message}`);
  };

  const lines = fs.readFileSync(filePath, 'utf8')
    .split(/\r?\n/u)
    .filter(line => line.trim().length > 0);
  const records = [];

  for (let index = 0; index < lines.length; index += 1) {
    try {
      records.push(JSON.parse(lines[index]));
    } catch (error) {
      fail(`${relative(filePath)}:${index + 1}`, `invalid JSON: ${error.message}`);
    }
  }

  const sessionNext = new Map();
  const workflowNext = new Map();
  const rootNext = new Map();
  const eventRecords = new Map();
  const watchRevisions = new Map();
  const workspaceRevisions = new Map();
  const graphRevisions = new Map();
  const graphSourceCursors = new Map();
  const rootJournals = new Map();
  const sessionActive = new Map();
  const workflows = new Map();
  const runs = new Map();
  const tools = new Map();
  const interrupts = new Map();
  const leases = new Map();
  const executionTokens = new Map();

  for (let index = 0; index < records.length; index += 1) {
    const record = records[index];
    const at = `${relative(filePath)}:${index + 1}`;

    requireFields(record, ['type', 'protocolVersion'], at, fail);
    if (record.protocolVersion !== 'narad/v1') fail(at, 'protocolVersion must be narad/v1.');
    validateSchema(record, at, fail);
    validateProfileSemantics(record, at, fail);

    if (controlTypes.has(record.type)) {
      for (const forbidden of ['eventId', 'sessionSequence', 'workflowSequence', 'sequence', 'revision']) {
        if (Object.hasOwn(record, forbidden)) fail(at, `control message must not contain ${forbidden}.`);
      }
      validateControl(record, at, leases, tools, executionTokens, fail);
      continue;
    }

    if (hydrateTypes.has(record.type)) {
      requireFields(record, ['timestamp', 'sessionId'], at, fail);
      for (const forbidden of ['eventId', 'sessionSequence', 'workflowSequence', 'sequence']) {
        if (Object.hasOwn(record, forbidden)) fail(at, `hydrate record must not contain ${forbidden}.`);
      }
      if (record.type === 'messages.snapshot') {
        requireFields(record, ['snapshotId', 'coversThroughSessionSequence', 'messages'], at, fail);
        sessionNext.set(record.sessionId, record.coversThroughSessionSequence + 1);
      } else {
        validateGraphShape(record, at, fail);
        const revisionKey = graphRevisionKey(record);
        const previousGraphRevision = graphRevisions.get(revisionKey) ?? 0;
        validateScopedGraph(record, at, rootJournals, previousGraphRevision, graphSourceCursors, fail);
        trackGraphRevision(record, at, graphRevisions, fail);
      }
      continue;
    }

    requireFields(record, ['eventId', 'timestamp'], at, fail);
    if (watchTypes.has(record.type)) {
      const scopeId = record.workspaceId ?? record.sessionId;
      const eventKey = `${scopeId}\u0000${record.eventId}`;
      const priorEvent = eventRecords.get(eventKey);
      if (priorEvent) {
        if (!isDeepStrictEqual(priorEvent, record)) fail(at, 'watch event identity was reused with different data.');
        continue;
      }
      eventRecords.set(eventKey, record);

      if (workspaceWatchTypes.has(record.type)) {
        requireFields(record, ['workspaceId'], at, fail);
        const previousRevision = workspaceRevisions.get(record.workspaceId) ?? 0;
        if (record.revision <= previousRevision) {
          fail(at, 'workspace watch revision must increase monotonically per workspaceId.');
        }
        workspaceRevisions.set(record.workspaceId, record.revision);
        continue;
      }

      const revisionKey = record.nodeId
        ? `node\u0000${record.workflowId}\u0000${record.nodeId}`
        : record.runId
          ? `run\u0000${record.rootRunId}\u0000${record.runId}`
          : record.workflowId
            ? `workflow\u0000${record.workflowId}`
            : `session\u0000${record.sessionId}`;
      const previousRevision = watchRevisions.get(revisionKey) ?? 0;
      if (record.revision <= previousRevision) fail(at, 'watch revision must increase monotonically.');
      watchRevisions.set(revisionKey, record.revision);
      continue;
    }

    requireFields(record, ['sessionId', 'sessionSequence'], at, fail);
    const eventKey = `${record.sessionId}\u0000${record.eventId}`;
    const priorEvent = eventRecords.get(eventKey);
    if (priorEvent) {
      if (!isDeepStrictEqual(priorEvent, record)) fail(at, 'event identity was reused with different data.');
      continue;
    }
    eventRecords.set(eventKey, record);
    checkContiguous(sessionNext, record.sessionId, record.sessionSequence, at, 'sessionSequence', fail);

    if (record.workflowId) {
      requireFields(record, ['workflowSequence'], at, fail);
      checkContiguous(workflowNext, record.workflowId, record.workflowSequence, at, 'workflowSequence', fail);
    } else if (Object.hasOwn(record, 'workflowSequence')) {
      fail(at, 'workflowSequence requires workflowId.');
    }

    if (record.type.startsWith('workflow.')) {
      validateWorkflowAuthority(record, at, workflows, sessionActive, fail);
    }

    if (record.runId) {
      requireFields(record, ['rootRunId', 'sequence'], at, fail);
      checkContiguous(rootNext, record.rootRunId, record.sequence, at, 'sequence', fail);
      if (record.type === 'progress.recorded') {
        validateProgressRecord(record, at, runs, fail);
      } else {
        validateRunAuthority(record, at, runs, tools, interrupts, executionTokens, workflows, sessionActive, fail);
      }
      noteRootJournalEvent(record, at, rootJournals, fail);
    }
  }

  const runTreeRecords = records.filter(record =>
    record.rootRunId && Number.isInteger(record.sequence) && runTreeTypes.has(record.type));
  for (const request of runTreeRecords.filter(record => record.type === 'interrupt.requested')) {
    const next = runTreeRecords.find(record =>
      record.rootRunId === request.rootRunId && record.sequence === request.sequence + 1);
    if (!next || next.type !== 'run.paused' || next.waitId !== request.waitId ||
        next.interruptId !== request.interruptId) {
      traceFailures.push(`${relative(filePath)}: interrupt.requested must be immediately followed by matching run.paused with no interleaved run-tree events.`);
    }
  }

  return traceFailures;
}

function validateSchema(record, at, fail) {
  const schemaName = controlTypes.has(record.type)
    ? 'narad-control.schema.json'
    : hydrateTypes.has(record.type)
      ? 'narad-hydrate.schema.json'
      : coreTypes.has(record.type)
        ? 'narad-core-event.schema.json'
        : 'narad-profile-event.schema.json';
  const validate = validators.get(schemaName);
  if (!validate) {
    fail(at, `no compiled schema available for ${record.type}.`);
    return;
  }
  if (!validate(record)) {
    const detail = validate.errors?.map(error => `${error.instancePath || '/'} ${error.message}`).join('; ');
    fail(at, `schema ${schemaName} rejected record: ${detail}`);
  }
}

function validateProfileSemantics(record, at, fail) {
  if (['state.delta', 'ui.state.delta'].includes(record.type) &&
      record.revision !== record.baseRevision + 1) {
    fail(at, `${record.type} revision must equal baseRevision + 1.`);
  }
  if (record.type === 'graph.delta' && record.revision !== record.baseRevision + 1) {
    fail(at, 'graph.delta revision must equal baseRevision + 1.');
  }
  if (Array.isArray(record.patch)) {
    for (const operation of record.patch) {
      if (['move', 'copy'].includes(operation.op) && typeof operation.from !== 'string') {
        fail(at, `JSON Patch ${operation.op} operation requires from.`);
      }
      if (['add', 'replace', 'test'].includes(operation.op) &&
          !Object.hasOwn(operation, 'value')) {
        fail(at, `JSON Patch ${operation.op} operation requires value.`);
      }
    }
  }
}

function validateGraphShape(record, at, fail) {
  requireFields(record, ['scope', 'graphSchemaVersion', 'revision'], at, fail);
  if (record.scope === 'run') {
    requireFields(record, ['rootRunId', 'sourceRunCursor'], at, fail);
    if (Object.hasOwn(record, 'workflowId') || Object.hasOwn(record, 'workflowSequence')) {
      fail(at, 'run-scoped graph must not include workflow ordering fields.');
    }
    if (record.type === 'graph.delta') {
      requireFields(record, ['baseRevision', 'patch'], at, fail);
    } else {
      requireFields(record, ['rootNodeId', 'nodes', 'edges'], at, fail);
    }
  } else if (record.scope === 'workflow') {
    requireFields(record, ['workflowId', 'sourceWorkflowCursor'], at, fail);
    if (record.type === 'graph.delta') {
      requireFields(record, ['baseRevision', 'patch'], at, fail);
    } else {
      requireFields(record, ['rootNodeId', 'nodes', 'edges'], at, fail);
    }
  } else {
    fail(at, 'graph events require scope run or workflow.');
  }
}

function trackGraphRevision(record, at, graphRevisions, fail) {
  const revisionKey = graphRevisionKey(record);
  const previousRevision = graphRevisions.get(revisionKey) ?? 0;
  if (record.type === 'graph.snapshot') {
    if (record.revision <= previousRevision) {
      fail(at, 'graph snapshot revision must increase within its scope key.');
    }
  } else if (record.revision !== record.baseRevision + 1 || record.baseRevision !== previousRevision) {
    fail(at, 'graph delta revision must continue contiguously from the latest graph revision.');
  }
  graphRevisions.set(revisionKey, record.revision);
}

function validateScopedGraph(record, at, rootJournals, previousGraphRevision, graphSourceCursors, fail) {
  if (record.scope === 'run') {
    const journal = rootJournals.get(record.rootRunId);
    if (!journal?.started) {
      fail(at, 'run-scoped graph requires an already-started rootRunId.');
      return;
    }
    if (journal.sessionId !== record.sessionId) {
      fail(at, 'run-scoped graph rootRunId must belong to the same session.');
    }

    requireFields(record.sourceRunCursor, ['sequence'], `${at}:sourceRunCursor`, fail);
    if (Object.hasOwn(record.sourceRunCursor, 'rootRunId') &&
        record.sourceRunCursor.rootRunId !== record.rootRunId) {
      fail(at, 'sourceRunCursor.rootRunId must match rootRunId when present.');
    }
    validateGraphCursor(
      record.sourceRunCursor.sequence,
      journal.latestSequence,
      graphRevisionKey(record),
      previousGraphRevision,
      graphSourceCursors,
      record.revision,
      at,
      fail,
    );
    return;
  }

  if (record.scope === 'workflow') {
    requireFields(record.sourceWorkflowCursor, ['sequence'], `${at}:sourceWorkflowCursor`, fail);
    if (Object.hasOwn(record.sourceWorkflowCursor, 'workflowId') &&
        record.sourceWorkflowCursor.workflowId !== record.workflowId) {
      fail(at, 'sourceWorkflowCursor.workflowId must match workflowId when present.');
    }
    validateGraphCursor(
      record.sourceWorkflowCursor.sequence,
      Number.MAX_SAFE_INTEGER,
      graphRevisionKey(record),
      previousGraphRevision,
      graphSourceCursors,
      record.revision,
      at,
      fail,
    );
  }
}

function validateGraphCursor(
  cursorSequence,
  latestSequence,
  revisionKey,
  previousGraphRevision,
  graphSourceCursors,
  graphRevision,
  at,
  fail,
) {
  if (!Number.isInteger(cursorSequence) || cursorSequence < 1) {
    fail(at, 'graph source cursor sequence must be a positive committed sequence.');
  } else if (cursorSequence > latestSequence) {
    fail(at, 'graph source cursor sequence must not exceed the latest committed source journal sequence.');
  }

  const previousCursor = graphSourceCursors.get(revisionKey) ?? 0;
  if (graphRevision > previousGraphRevision && cursorSequence < previousCursor) {
    fail(at, 'graph source cursor sequence must not regress across increasing graph revisions.');
  }
  graphSourceCursors.set(revisionKey, Math.max(previousCursor, cursorSequence));
}

function noteRootJournalEvent(record, at, rootJournals, fail) {
  if (!record.rootRunId || !Number.isInteger(record.sequence)) return;

  let journal = rootJournals.get(record.rootRunId);
  if (!journal) {
    if (record.type !== 'run.started') {
      fail(at, 'run-tree event appeared before root run.started.');
      return;
    }
    journal = { sessionId: record.sessionId, latestSequence: 0, started: false };
    rootJournals.set(record.rootRunId, journal);
  }

  if (record.type === 'run.started' && record.runId === record.rootRunId) {
    journal.started = true;
    journal.sessionId = record.sessionId;
  }

  journal.latestSequence = Math.max(journal.latestSequence, record.sequence);
}

function validateProgressRecord(record, at, runs, fail) {
  requireFields(record, ['progressId', 'category', 'label', 'status', 'privacy'], at, fail);
  const run = runs.get(record.runId);
  if (!run) {
    fail(at, 'progress.recorded requires an already-started run.');
    return;
  }
  if (run.sessionId !== record.sessionId || run.rootRunId !== record.rootRunId) {
    fail(at, 'progress.recorded must match the run session and root lineage.');
  }
  if (terminalTypes.has(run.state)) {
    fail(at, 'progress.recorded must not appear after the run is terminal.');
  } else if (run.state !== 'running' && run.state !== 'paused') {
    fail(at, 'progress.recorded requires the run to be running or paused.');
  }
}

function graphRevisionKey(record) {
  return record.scope === 'run'
    ? `graph-run\u0000${record.sessionId}\u0000${record.rootRunId}`
    : `graph-workflow\u0000${record.workflowId}`;
}

function validateControl(record, at, leases, tools, executionTokens, fail) {
  if (record.type === 'client.attached') {
    requireFields(record, ['clientExecutorId', 'leaseId', 'heartbeatIntervalMs', 'leaseExpiresAt'], at, fail);
    leases.set(record.clientExecutorId, record.leaseId);
    return;
  }
  if (record.type === 'server.heartbeat') return;

  requireFields(record, ['clientExecutorId', 'leaseId'], at, fail);
  const currentLease = leases.get(record.clientExecutorId);

  if (record.type === 'client.tool.execute') {
    requireFields(record, ['executionToken', 'executionTokenExpiresAt', 'toolCallId', 'toolName', 'args'], at, fail);
    const tool = tools.get(record.toolCallId);
    if (!tool || tool.executor !== 'client') fail(at, 'execution control has no open client tool call.');
    if (tool.clientExecutorId !== record.clientExecutorId) fail(at, 'execution control targets the wrong run executor.');
    if (tool.toolName !== record.toolName || !isDeepStrictEqual(tool.args, record.args)) {
      fail(at, 'execution control payload differs from tool.call.started.');
    }
    const existing = executionTokens.get(record.executionToken);
    const token = {
      toolCallId: record.toolCallId,
      clientExecutorId: record.clientExecutorId,
      leaseId: record.leaseId,
      expiresAt: record.executionTokenExpiresAt,
      finish: null,
    };
    if (existing) {
      if (!isDeepStrictEqual({ ...existing, finish: null }, token)) {
        fail(at, 'execution token was reused with different data.');
      }
      return;
    }
    if (currentLease !== record.leaseId) fail(at, 'new execution control uses an unknown or stale lease.');
    if (tool.acceptedToken && tool.acceptedToken !== record.executionToken) {
      fail(at, 'tool call received more than one execution token.');
    } else {
      tool.acceptedToken = record.executionToken;
      executionTokens.set(record.executionToken, token);
    }
  }
  if (record.type === 'client.tool.finish') {
    requireFields(record, ['executionToken', 'toolCallId', 'outcome', 'response'], at, fail);
    const token = executionTokens.get(record.executionToken);
    if (!token || token.toolCallId !== record.toolCallId ||
        token.clientExecutorId !== record.clientExecutorId || token.leaseId !== record.leaseId) {
      fail(at, 'finish uses an invalid execution token.');
      return;
    }
    const finish = { outcome: record.outcome, response: record.response };
    if (token.finish && !isDeepStrictEqual(token.finish, finish)) fail(at, 'execution token finish was replayed with different data.');
    else token.finish = finish;
  }
  if (record.type === 'client.heartbeat' && currentLease !== record.leaseId) {
    fail(at, 'heartbeat uses an unknown or stale lease.');
  }
}

function validateRunAuthority(
  record,
  at,
  runs,
  tools,
  interrupts,
  executionTokens,
  workflows,
  sessionActive,
  fail,
) {
  let run = runs.get(record.runId);

  if (record.type === 'run.started') {
    if (run) fail(at, 'run started more than once.');
    run = {
      state: 'running',
      sessionId: record.sessionId,
      rootRunId: record.rootRunId,
      workflowId: record.workflowId ?? null,
      waitId: null,
      clientExecutorId: record.clientExecutorId ?? null,
      parentRunId: record.parentRunId ?? null,
      openChildren: new Set(),
      openTools: new Set(),
      openInterrupt: null,
      stopRequested: false,
    };
    runs.set(record.runId, run);
    if (record.workflowId) {
      const workflow = workflows.get(record.workflowId);
      if (!workflow || workflow.state !== 'active') fail(at, 'workflow run requires an active workflow.');
      else {
        workflow.openRuns.add(record.runId);
        if (!record.parentRunId) {
          if (workflow.activeTopLevelRunId) fail(at, 'workflow already has an active top-level run.');
          else workflow.activeTopLevelRunId = record.runId;
        }
      }
    }
    if (record.parentRunId) {
      const parent = runs.get(record.parentRunId);
      if (!parent || terminalTypes.has(parent.state)) fail(at, 'nested run requires an open parent run.');
      else if (parent.state !== 'running') fail(at, 'cannot start a child while its parent is paused.');
      else if (parent.sessionId !== record.sessionId || parent.rootRunId !== record.rootRunId ||
          parent.workflowId !== (record.workflowId ?? null)) {
        fail(at, 'nested run must retain parent session, root journal, and workflow lineage.');
      }
      else if (parent.stopRequested) fail(at, 'cannot start a child after run.stop.requested.');
      else parent.openChildren.add(record.runId);
    } else if (!record.workflowId) {
      if (sessionActive.has(record.sessionId)) fail(at, 'session already has an active top-level execution.');
      else sessionActive.set(record.sessionId, { kind: 'run', id: record.runId });
    } else {
      const active = sessionActive.get(record.sessionId);
      if (!active || active.kind !== 'workflow' || active.id !== record.workflowId) {
        fail(at, 'workflow root run does not belong to the active session workflow.');
      }
    }
    return;
  }
  if (!run) {
    fail(at, `run authority ${record.type} appeared before run.started.`);
    return;
  }
  if (terminalTypes.has(run.state)) {
    fail(at, `event ${record.type} appeared after run terminal ${run.state}.`);
    return;
  }

  if (record.type === 'run.paused') {
    requireFields(record, ['waitId', 'reason', 'resumeMode'], at, fail);
    if (run.state !== 'running') fail(at, 'run.paused requires running state.');
    run.state = 'paused';
    run.waitId = record.waitId;
  } else if (record.type === 'run.resumed') {
    requireFields(record, ['waitId'], at, fail);
    if (run.state !== 'paused' || run.waitId !== record.waitId) fail(at, 'run.resumed must match the active pause.');
    if (run.openInterrupt) fail(at, 'run resumed with an open interrupt.');
    run.state = 'running';
    run.waitId = null;
  } else if (record.type === 'interrupt.requested') {
    requireFields(record, ['interruptId', 'waitId', 'kind', 'actions'], at, fail);
    if (run.openInterrupt) fail(at, 'Narad v1 permits one open interrupt per run.');
    run.openInterrupt = record.interruptId;
    interrupts.set(record.interruptId, { runId: record.runId, waitId: record.waitId, actions: record.actions });
  } else if (record.type === 'interrupt.resolved') {
    requireFields(record, ['interruptId', 'waitId', 'decision'], at, fail);
    const interrupt = interrupts.get(record.interruptId);
    if (!interrupt || interrupt.runId !== record.runId || interrupt.waitId !== record.waitId) {
      fail(at, 'interrupt resolution does not match an open interrupt.');
    } else if (record.decision !== 'expired' && !interrupt.actions.includes(record.decision)) {
      fail(at, 'interrupt decision was not offered by actions.');
    }
    interrupts.delete(record.interruptId);
    run.openInterrupt = null;
  } else if (record.type === 'tool.call.started') {
    requireFields(record, ['toolCallId', 'toolName', 'executor', 'args'], at, fail);
    if (tools.has(record.toolCallId)) fail(at, 'toolCallId reused.');
    if (run.state !== 'running') fail(at, 'cannot start a tool while the run is paused.');
    if (run.stopRequested) fail(at, 'cannot start a tool after run.stop.requested.');
    if (record.executor === 'client' && !run.clientExecutorId) fail(at, 'client tool requires run clientExecutorId.');
    tools.set(record.toolCallId, {
      runId: record.runId,
      executor: record.executor,
      clientExecutorId: run.clientExecutorId,
      toolName: record.toolName,
      args: record.args,
      acceptedToken: null,
    });
    run.openTools.add(record.toolCallId);
  } else if (record.type === 'tool.call.finished') {
    requireFields(record, ['toolCallId', 'outcome', 'response'], at, fail);
    const tool = tools.get(record.toolCallId);
    if (!tool || tool.runId !== record.runId || !run.openTools.has(record.toolCallId)) fail(at, 'tool finish has no matching open call.');
    if (tool?.executor === 'client') {
      const token = [...executionTokens.values()].find(value => value.toolCallId === record.toolCallId);
      const backendClosure = record.outcome !== 'success' &&
        ['client_unavailable', 'delivery_failed', 'client_execution_expired'].includes(
          record.code ?? record.response?.code,
        );
      const stopCancellation = record.outcome === 'cancelled' && run.stopRequested;
      if (!token?.finish) {
        if (!backendClosure && !stopCancellation) {
          fail(at, 'client tool terminal has no accepted finish, backend closure code, or stop cancellation.');
        }
      } else if (token.finish.outcome !== record.outcome || !isDeepStrictEqual(token.finish.response, record.response)) {
        fail(at, 'tool.call.finished differs from accepted client.tool.finish.');
      }
    }
    run.openTools.delete(record.toolCallId);
  } else if (record.type === 'run.stop.requested') {
    requireFields(record, ['targetRunId'], at, fail);
    const target = runs.get(record.targetRunId);
    if (!target || terminalTypes.has(target.state)) fail(at, 'run.stop.requested targets a missing or terminal run.');
    else if (target.sessionId !== record.sessionId || target.rootRunId !== record.rootRunId) {
      fail(at, 'run.stop.requested target must belong to the same session and root journal.');
    }
    else target.stopRequested = true;
  } else if (terminalTypes.has(record.type)) {
    if (run.state !== 'running') fail(at, 'run terminal requires running state.');
    if (run.waitId || run.openInterrupt || run.openTools.size > 0 || run.openChildren.size > 0) {
      fail(at, 'run terminal emitted with open work.');
    }
    run.state = record.type;
    if (run.parentRunId) runs.get(run.parentRunId)?.openChildren.delete(record.runId);
    if (record.workflowId) {
      const workflow = workflows.get(record.workflowId);
      workflow?.openRuns.delete(record.runId);
      if (!run.parentRunId && workflow?.activeTopLevelRunId === record.runId) {
        workflow.activeTopLevelRunId = null;
      }
    }
    else if (!run.parentRunId) sessionActive.delete(record.sessionId);
  }
}

function validateWorkflowAuthority(record, at, workflows, sessionActive, fail) {
  if (record.type === 'workflow.started') {
    if (workflows.has(record.workflowId)) fail(at, 'workflow started more than once.');
    if (sessionActive.has(record.sessionId)) fail(at, 'session already has an active top-level execution.');
    workflows.set(record.workflowId, { state: 'active', openRuns: new Set(), activeTopLevelRunId: null });
    sessionActive.set(record.sessionId, { kind: 'workflow', id: record.workflowId });
    return;
  }

  const workflow = workflows.get(record.workflowId);
  if (!workflow || workflow.state !== 'active') {
    fail(at, `${record.type} requires an active workflow.`);
    return;
  }
  if (['workflow.finished', 'workflow.error', 'workflow.cancelled'].includes(record.type)) {
    if (workflow.openRuns.size > 0) fail(at, 'workflow terminal emitted with open runs.');
    workflow.state = record.type;
    sessionActive.delete(record.sessionId);
  }
}

function requireFields(record, fields, at, fail) {
  for (const field of fields) {
    if (!Object.hasOwn(record, field)) fail(at, `missing required field ${field}.`);
  }
}

function checkContiguous(nextByScope, scope, value, at, field, fail) {
  const expected = nextByScope.get(scope) ?? 1;
  if (value !== expected) fail(at, `${field} expected ${expected}, received ${value}.`);
  nextByScope.set(scope, value + 1);
}

function relative(filePath) {
  return path.relative(repoRoot, filePath).replaceAll('\\', '/');
}

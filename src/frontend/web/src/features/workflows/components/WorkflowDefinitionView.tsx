import axios from 'axios';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select } from '@/components/ui/select';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { PageIntro } from '@/components/layout/PageIntro';
import { PageState } from '@/components/layout/PageState';
import { useAgents } from '@/features/settings';
import {
  normalizeWorkspaceFilePath,
  uploadAgentFile,
  WorkspaceFilePickerDialog,
  type AgentFilesPanelAgent,
  type AgentFileUploadResponse,
} from '@/features/agent-files';
import { X } from 'lucide-react';
import React from 'react';
import { Link, useNavigate } from 'react-router-dom';
import {
  useCreateWorkflow,
  useStartWorkflowInstance,
  useUpdateWorkflow,
  useWorkflow,
} from '../api/queries';
import { testWorkflowStep } from '../api/workflows';
import type { WorkflowDefinition } from '../schemas/workflowSchema';
import {
  coerceFieldValue,
  describeDraftProblems,
  inputFieldsFromDefinition,
  isSafeFieldName,
  fieldDisplayName,
  stepsFromDefinition,
  toSaveWorkflowRequest,
  workflowTestInput,
  type WorkflowFieldDraft,
  type WorkflowStepDraft,
} from '../model/workflowDraft';
import {
  addStepOn,
  describeStepProblems,
  effectiveTransitions,
  extendPath,
  workflowDisplayNumbers,
  type ConnectionRef,
} from '../model/workflowCanvas';

const WorkflowCanvas = React.lazy(() =>
  import('./WorkflowCanvas').then(({ WorkflowCanvas: Canvas }) => ({ default: Canvas }))
);

const newWorkflowDraftDefinition: WorkflowDefinition = {
  id: 'new',
  ownerUserId: '',
  name: 'Untitled workflow',
  governed: false,
  revision: 0,
  inputSchema: { type: 'object', properties: {} },
  workingSchema: { type: 'object', properties: {} },
  outputSchema: { type: 'object', properties: {} },
  nodes: [],
  createdAt: '',
  updatedAt: '',
  diagram: null,
};

interface WorkflowDefinitionViewProps {
  workflowId: string;
  focusRun?: boolean;
  onRunFocused?: () => void;
  workflowsListOpen?: boolean;
  onToggleWorkflowsList?: () => void;
  onDirtyChange?: (dirty: boolean) => void;
  onNavigateWithPrompt?: (
    path: string,
    state?: { targetType: 'workflow'; targetId: string }
  ) => void;
  onFirstSaveNavigate?: (path: string) => void;
  isNew?: boolean;
  workflowActions?: React.ReactNode;
  readOnlyOverview?: boolean;
  overviewRuns?: React.ReactNode;
}

export const WorkflowDefinitionView: React.FC<WorkflowDefinitionViewProps> = ({
  workflowId,
  focusRun = false,
  onRunFocused,
  workflowsListOpen,
  onToggleWorkflowsList,
  onDirtyChange,
  onNavigateWithPrompt,
  onFirstSaveNavigate,
  isNew = false,
  workflowActions,
  readOnlyOverview = false,
  overviewRuns,
}) => {
  const navigate = useNavigate();
  const workflowQuery = useWorkflow(isNew ? undefined : workflowId);
  const workflow = workflowQuery.data ?? (isNew ? newWorkflowDraftDefinition : undefined);
  const isLoading = !isNew && workflowQuery.isLoading;
  const error = isNew ? null : workflowQuery.error;
  const { data: agents = [] } = useAgents();
  const save = useUpdateWorkflow(workflowId);
  const createWorkflow = useCreateWorkflow();
  const start = useStartWorkflowInstance(workflowId);
  const savedWorkflowId = React.useRef(workflowId);
  const [name, setName] = React.useState('');
  const [steps, setSteps] = React.useState<WorkflowStepDraft[]>([]);
  const [inputFields, setInputFields] = React.useState<WorkflowFieldDraft[]>([]);
  const [selectedCanvasKey, setSelectedCanvasKey] = React.useState('');
  const [canvasEditError, setCanvasEditError] = React.useState<string | null>(null);
  const [showValidation, setShowValidation] = React.useState(false);
  const [runValues, setRunValues] = React.useState<Record<string, string>>({});
  const [correlationKey, setCorrelationKey] = React.useState('');
  const [runError, setRunError] = React.useState<string | null>(null);
  const [runFilePaths, setRunFilePaths] = React.useState<string[]>([]);
  const [runFilePickerOpen, setRunFilePickerOpen] = React.useState(false);
  const [runFileUploadPending, setRunFileUploadPending] = React.useState(false);
  const [runDialogOpen, setRunDialogOpen] = React.useState(false);
  const [saveRunPromptOpen, setSaveRunPromptOpen] = React.useState(false);
  const [saveError, setSaveError] = React.useState<string | null>(null);
  const [saveMessage, setSaveMessage] = React.useState<string | null>(null);
  type DraftSnapshot = {
    name: string;
    inputFields: WorkflowFieldDraft[];
    steps: WorkflowStepDraft[];
  };
  const history = React.useRef<{
    past: DraftSnapshot[];
    present: DraftSnapshot;
    future: DraftSnapshot[];
  }>({
    past: [],
    present: { name: '', inputFields: [], steps: [] },
    future: [],
  });
  const historyTimer = React.useRef<ReturnType<typeof setTimeout> | null>(null);
  const historyPending = React.useRef(false);
  const pendingSnapshot = React.useRef<DraftSnapshot | null>(null);
  const savedSnapshot = React.useRef<DraftSnapshot>({ name: '', inputFields: [], steps: [] });
  const [historyRevision, setHistoryRevision] = React.useState(0);
  const previousReadOnlyOverview = React.useRef(readOnlyOverview);
  const hydratedId = React.useRef<string | null>(null);
  const runFormRef = React.useRef<HTMLFormElement>(null);
  const runFileUploadInputRef = React.useRef<HTMLInputElement>(null);
  const firstStepAgentId = workflow?.nodes[0]?.agentId ?? '';
  const firstStepAgent = agents.find((agent) => agent.id === firstStepAgentId);
  const filePickerAgent = React.useMemo<AgentFilesPanelAgent | null>(
    () =>
      firstStepAgentId
        ? {
            id: firstStepAgentId,
            name: firstStepAgent?.name ?? 'Agent',
            sandboxEnabled: Boolean(firstStepAgent?.sandbox?.enabled),
          }
        : null,
    [firstStepAgent?.name, firstStepAgent?.sandbox?.enabled, firstStepAgentId]
  );

  React.useEffect(() => {
    if (previousReadOnlyOverview.current !== readOnlyOverview) {
      setSelectedCanvasKey('');
      previousReadOnlyOverview.current = readOnlyOverview;
    }
  }, [readOnlyOverview]);

  React.useEffect(() => {
    if (focusRun) {
      setRunDialogOpen(true);
      onRunFocused?.();
    }
  }, [focusRun, onRunFocused]);

  React.useEffect(() => {
    if (!workflow || hydratedId.current === workflow.id) {
      return;
    }
    hydratedId.current = workflow.id;
    const loadedSteps = stepsFromDefinition(workflow);
    setName(workflow.name);
    setSteps(loadedSteps);
    const fields = inputFieldsFromDefinition(workflow);
    setInputFields(fields);
    const loadedSnapshot = { name: workflow.name, inputFields: fields, steps: loadedSteps };
    history.current = {
      past: [],
      present: loadedSnapshot,
      future: [],
    };
    savedSnapshot.current = loadedSnapshot;
    historyPending.current = false;
    pendingSnapshot.current = null;
    setSelectedCanvasKey('');
    setHistoryRevision((revision) => revision + 1);
    setRunValues((current) => {
      const next: Record<string, string> = Object.create(null);
      for (const field of fields) {
        if (!isSafeFieldName(field.name)) {
          continue;
        }
        const previous = Object.prototype.hasOwnProperty.call(current, field.name)
          ? current[field.name]
          : '';
        next[field.name] = previous ?? '';
      }
      return next;
    });
  }, [workflow]);

  const snapshot = React.useMemo<DraftSnapshot>(
    () => ({ name, inputFields, steps }),
    [name, inputFields, steps]
  );
  const isDirty = JSON.stringify(snapshot) !== JSON.stringify(savedSnapshot.current);
  React.useEffect(() => onDirtyChange?.(isDirty), [isDirty, onDirtyChange]);
  React.useEffect(() => setSaveError(null), [snapshot]);
  React.useEffect(() => {
    if (!isDirty) return;
    const warnBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = 'Leave without saving?';
    };
    window.addEventListener('beforeunload', warnBeforeUnload);
    return () => window.removeEventListener('beforeunload', warnBeforeUnload);
  }, [isDirty]);
  const structuralSignature = React.useMemo(
    () =>
      JSON.stringify({
        inputKeys: inputFields.map((field) => field.key),
        stepKeys: steps.map((step) => step.key),
        stepFields: steps.map((step) => [
          step.key,
          step.writes.map((field) => field.key),
          step.handoffs.map((handoff) => handoff.key),
        ]),
        choices: steps.map((step) => [
          step.key,
          step.choices.map((rule) => [rule.key, rule.goToKey]),
          step.otherwiseKey,
        ]),
        transitions: effectiveTransitions(steps),
      }),
    [inputFields, steps]
  );
  const lastStructuralSignature = React.useRef(structuralSignature);

  React.useEffect(() => {
    const current = JSON.stringify(snapshot);
    if (current === JSON.stringify(history.current.present)) {
      if (historyPending.current) {
        if (historyTimer.current) clearTimeout(historyTimer.current);
        historyTimer.current = null;
        historyPending.current = false;
        pendingSnapshot.current = null;
      }
      lastStructuralSignature.current = structuralSignature;
      return;
    }
    const structural = structuralSignature !== lastStructuralSignature.current;
    lastStructuralSignature.current = structuralSignature;
    if (historyTimer.current) clearTimeout(historyTimer.current);
    if (structural) {
      if (historyPending.current && pendingSnapshot.current) {
        const pending = pendingSnapshot.current;
        history.current.past = [...history.current.past, history.current.present].slice(-50);
        history.current.present = pending;
      }
      history.current.past = [...history.current.past, history.current.present].slice(-50);
      history.current.present = snapshot;
      history.current.future = [];
      historyPending.current = false;
      pendingSnapshot.current = null;
      setHistoryRevision((revision) => revision + 1);
      return;
    }
    historyPending.current = true;
    pendingSnapshot.current = snapshot;
    historyTimer.current = setTimeout(() => {
      const pending = pendingSnapshot.current ?? snapshot;
      history.current.past = [...history.current.past, history.current.present].slice(-50);
      history.current.present = pending;
      history.current.future = [];
      historyPending.current = false;
      pendingSnapshot.current = null;
      setHistoryRevision((revision) => revision + 1);
    }, 1000);
  }, [snapshot, structuralSignature]);

  const flushPendingHistory = React.useCallback(() => {
    if (!historyPending.current || !pendingSnapshot.current) return;
    if (historyTimer.current) clearTimeout(historyTimer.current);
    history.current.past = [...history.current.past, history.current.present].slice(-50);
    history.current.present = pendingSnapshot.current;
    history.current.future = [];
    historyPending.current = false;
    pendingSnapshot.current = null;
    setHistoryRevision((revision) => revision + 1);
  }, []);

  const undo = React.useCallback(() => {
    setCanvasEditError(null);
    if (historyPending.current) {
      if (historyTimer.current) clearTimeout(historyTimer.current);
      history.current.present = pendingSnapshot.current ?? snapshot;
      historyPending.current = false;
      pendingSnapshot.current = null;
    }
    const previous = history.current.past.pop();
    if (!previous) return;
    const selectedIndex = steps.findIndex((step) => step.key === selectedCanvasKey);
    history.current.future.unshift(history.current.present);
    history.current.present = previous;
    setName(previous.name);
    setInputFields(previous.inputFields);
    setSteps(previous.steps);
    if (selectedCanvasKey && selectedCanvasKey !== '__workflow_start__') {
      setSelectedCanvasKey(previous.steps[selectedIndex]?.key ?? '__workflow_start__');
    }
    setHistoryRevision((revision) => revision + 1);
  }, [selectedCanvasKey, snapshot, steps]);

  const redo = React.useCallback(() => {
    setCanvasEditError(null);
    const next = history.current.future.shift();
    if (!next) return;
    const selectedIndex = steps.findIndex((step) => step.key === selectedCanvasKey);
    history.current.past = [...history.current.past, history.current.present].slice(-50);
    history.current.present = next;
    setName(next.name);
    setInputFields(next.inputFields);
    setSteps(next.steps);
    if (selectedCanvasKey && selectedCanvasKey !== '__workflow_start__') {
      setSelectedCanvasKey(next.steps[selectedIndex]?.key ?? '__workflow_start__');
    }
    setHistoryRevision((revision) => revision + 1);
  }, [selectedCanvasKey, steps]);

  React.useEffect(() => {
    const handleHistoryKeys = (event: KeyboardEvent) => {
      const target = event.target;
      if (
        target instanceof HTMLElement &&
        target.closest('input, textarea, select, [contenteditable="true"]')
      )
        return;
      if (!(event.ctrlKey || event.metaKey)) return;
      const key = event.key.toLowerCase();
      if (key === 'y' || (key === 'z' && event.shiftKey)) {
        event.preventDefault();
        redo();
      } else if (key === 'z') {
        event.preventDefault();
        undo();
      }
    };
    document.addEventListener('keydown', handleHistoryKeys);
    return () => document.removeEventListener('keydown', handleHistoryKeys);
  }, [redo, undo]);

  const saveDraft = async (): Promise<boolean> => {
    setShowValidation(true);
    setCanvasEditError(null);
    setSaveError(null);
    setSaveMessage(null);
    const problem = describeDraftProblems(
      name,
      steps,
      inputFields,
      agents,
      workflow?.governed ?? false
    );
    if (problem) {
      setSaveError(problem);
      const stepNumber = /^Step (\d+):/.exec(problem)?.[1];
      if (stepNumber) {
        const displayNumbers = workflowDisplayNumbers(steps);
        const problemStep = steps.find(
          (step) => displayNumbers.get(step.key) === Number(stepNumber)
        );
        if (problemStep) setSelectedCanvasKey(problemStep.key);
        return false;
      }
      const stepProblem = Object.values(
        describeStepProblems(steps, inputFields, agents, workflow?.governed ?? false, true)
          .byStepKey
      )
        .flat()
        .find((candidate) => candidate.message === problem);
      if (stepProblem?.stepKey) setSelectedCanvasKey(stepProblem.stepKey);
      return false;
    }

    try {
      const request = toSaveWorkflowRequest(name, steps, workflow, inputFields);
      const saved = isNew
        ? await createWorkflow.mutateAsync(request)
        : await save.mutateAsync(request);
      savedWorkflowId.current = saved.id;
      flushPendingHistory();
      let cleanSnapshot = snapshot;
      if (isNew) {
        const selectedStepIndex = steps.findIndex((step) => step.key === selectedCanvasKey);
        const loadedSteps = stepsFromDefinition(saved);
        const loadedFields = inputFieldsFromDefinition(saved);
        cleanSnapshot = { name: saved.name, inputFields: loadedFields, steps: loadedSteps };
        setName(cleanSnapshot.name);
        setInputFields(loadedFields);
        setSteps(loadedSteps);
        setSelectedCanvasKey(loadedSteps[selectedStepIndex]?.key ?? '');
        hydratedId.current = saved.id;
        history.current = { past: [], present: cleanSnapshot, future: [] };
      } else {
        cleanSnapshot = {
          name: saved.name,
          inputFields: inputFields.map((field) => ({ ...field, newField: false })),
          steps: steps.map((step) => ({
            ...step,
            writes: step.writes.map((field) => ({ ...field, newField: false })),
          })),
        };
        setName(cleanSnapshot.name);
        setInputFields(cleanSnapshot.inputFields);
        setSteps(cleanSnapshot.steps);
        history.current.present = cleanSnapshot;
        history.current.future = [];
      }
      historyPending.current = false;
      pendingSnapshot.current = null;
      savedSnapshot.current = cleanSnapshot;
      setShowValidation(false);
      setCanvasEditError(null);
      setHistoryRevision((revision) => revision + 1);
      setSaveMessage('Saved.');
      if (isNew) {
        const path = `/workflows/${saved.id}/edit`;
        if (onFirstSaveNavigate) onFirstSaveNavigate(path);
        else navigate(path, { replace: true });
      }
      return true;
    } catch (saveFailure) {
      const status = axios.isAxiosError(saveFailure) ? saveFailure.response?.status : undefined;
      setSaveError(
        status === 400
          ? "Couldn't save this workflow. Check the steps and try again."
          : "Couldn't save this workflow. Try again."
      );
      return false;
    }
  };

  const onSave = async (event: React.FormEvent) => {
    event.preventDefault();
    const creating = isNew;
    if (!(await saveDraft()) || !creating) return;
  };

  const onRun = async (event: React.FormEvent) => {
    event.preventDefault();
    if (isDirty) {
      setRunDialogOpen(false);
      setSaveRunPromptOpen(true);
      return;
    }
    setRunError(null);
    const missing = inputFields.filter(
      (field) => field.required && !(runValues[field.name] ?? '').trim()
    );
    if (missing.length > 0) {
      setRunError(`Fill in ${missing.map(fieldDisplayName).join(', ')}.`);
      return;
    }

    try {
      const instance = await start.mutateAsync({
        input: Object.fromEntries(
          inputFields
            .map((field) => [field.name, (runValues[field.name] ?? '').trim()] as const)
            .filter(([, value]) => value)
            .map(([key, value]) => {
              const field = inputFields.find((item) => item.name === key);
              return [key, coerceFieldValue(field?.type ?? 'string', value)];
            })
        ),
        correlationKey: correlationKey.trim() || undefined,
        files: runFilePaths,
      });
      navigate(`/workflows/${workflowId}/instances/${instance.id}`);
    } catch (startError) {
      const detail = axios.isAxiosError<{ detail?: unknown; message?: unknown }>(startError)
        ? (startError.response?.data?.detail ?? startError.response?.data?.message)
        : null;
      setRunError(
        typeof detail === 'string' && detail.length < 160
          ? detail
          : 'Could not start this run. Check the information and try again.'
      );
    }
  };

  const selectRunFile = (path: string) => {
    const normalizedPath = normalizeWorkspaceFilePath(path);
    if (normalizedPath)
      setRunFilePaths((current) =>
        current.includes(normalizedPath) ? current : [...current, normalizedPath]
      );
  };

  const uploadRunFile = async (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.currentTarget.files?.[0];
    event.currentTarget.value = '';
    if (!file || !firstStepAgentId) return;

    setRunError(null);
    setRunFileUploadPending(true);
    try {
      let uploaded: AgentFileUploadResponse;
      try {
        uploaded = await uploadAgentFile(firstStepAgentId, '', file);
      } catch (uploadFailure) {
        if (!axios.isAxiosError(uploadFailure) || uploadFailure.response?.status !== 409) {
          throw uploadFailure;
        }
        const body: unknown = uploadFailure.response.data;
        if (
          typeof body === 'object' &&
          body !== null &&
          'reasonCode' in body &&
          body.reasonCode === 'sandbox_disabled'
        ) {
          setRunError(`Turn on files for ${firstStepAgent?.name ?? 'the first step agent'} first.`);
          return;
        }
        if (!window.confirm('Replace the file already there?')) return;
        uploaded = await uploadAgentFile(firstStepAgentId, '', file, true);
      }
      setRunFilePaths((current) =>
        current.includes(uploaded.path) ? current : [...current, uploaded.path]
      );
    } catch (uploadFailure) {
      setRunError('Could not upload this file. Try again.');
    } finally {
      setRunFileUploadPending(false);
    }
  };

  const updateStep = (key: string, patch: Partial<WorkflowStepDraft>) => {
    setSteps((current) => current.map((step) => (step.key === key ? { ...step, ...patch } : step)));
  };

  const applyCanvasEdit = (result: ReturnType<typeof addStepOn>, selectCreated = true) => {
    setCanvasEditError(null);
    if (!result.ok) {
      setCanvasEditError(result.reason);
      return;
    }
    const created = result.steps.find(
      (candidate) => !steps.some((step) => step.key === candidate.key)
    );
    setSteps(result.steps);
    if (created && selectCreated) setSelectedCanvasKey(created.key);
  };

  const addCanvasStep = (ref: ConnectionRef, agentId: string) => {
    applyCanvasEdit(addStepOn(steps, ref, agentId));
  };

  const extendCanvasPath = (key: string, agentId: string) => {
    applyCanvasEdit(extendPath(steps, key, agentId));
  };

  const setRunField = (fieldName: string, value: string) => {
    if (!isSafeFieldName(fieldName)) {
      return;
    }
    setRunValues((current) => {
      const next: Record<string, string> = Object.create(null);
      for (const [key, existing] of Object.entries(current)) {
        if (isSafeFieldName(key)) {
          next[key] = existing;
        }
      }
      next[fieldName] = value;
      return next;
    });
  };

  if (isLoading) {
    return <PageState message="Loading workflow…" />;
  }

  if (error || !workflow) {
    return (
      <PageState
        message={error ? "Couldn't load this workflow. Try again." : 'Workflow not found.'}
      />
    );
  }

  return (
    <div className="page-shell mx-0 w-full max-w-none gap-6">
      <PageIntro
        kicker="Workflows"
        title={name.trim() || workflow.name}
        description={
          inputFields.length
            ? "Fill in the start fields when you run it. Each step's results are passed to the next steps."
            : "Each step's results are passed to the next steps."
        }
        actionsClassName={readOnlyOverview ? 'w-full basis-full' : undefined}
        actions={
          <div className="flex flex-wrap gap-2">
            {workflowActions}
            {readOnlyOverview ? (
              <Button type="button" onClick={() => setRunDialogOpen(true)}>
                Run workflow
              </Button>
            ) : null}
            {readOnlyOverview ? (
              <Button
                type="button"
                variant="outline"
                onClick={() =>
                  navigate('/autopilot', {
                    state: { targetType: 'workflow', targetId: workflowId },
                  })
                }
              >
                Autopilot
              </Button>
            ) : null}
            {readOnlyOverview ? (
              <Button
                type="button"
                variant="outline"
                className="hidden sm:inline-flex"
                onClick={() => navigate(`/workflows/${workflowId}/edit`)}
              >
                Edit workflow
              </Button>
            ) : null}
            {!readOnlyOverview && !isNew ? (
              <Button
                variant="outline"
                onClick={() => {
                  const path = '/autopilot';
                  if (onNavigateWithPrompt) {
                    onNavigateWithPrompt(path, { targetType: 'workflow', targetId: workflowId });
                    return;
                  }
                  if (isDirty && !window.confirm('Leave without saving?')) return;
                  onDirtyChange?.(false);
                  navigate(path, { state: { targetType: 'workflow', targetId: workflowId } });
                }}
              >
                Autopilot
              </Button>
            ) : null}
            <Button variant="outline" asChild>
              <Link to="/workflows">Back to workflows</Link>
            </Button>
          </div>
        }
      />

      <form className="space-y-4" onSubmit={(event) => void onSave(event)}>
        {!readOnlyOverview ? (
          <div
            className="workflow-canvas-actions sticky top-0 z-20 flex flex-wrap items-center gap-2 border-b bg-background/95 py-3 backdrop-blur"
            data-history-revision={historyRevision}
          >
            <span className="mr-auto truncate text-sm font-semibold" aria-label="Workflow name">
              {name.trim() || workflow.name}
            </span>
            {!readOnlyOverview && onToggleWorkflowsList ? (
              <Button
                type="button"
                variant="outline"
                aria-expanded={workflowsListOpen}
                onClick={onToggleWorkflowsList}
              >
                {workflowsListOpen ? 'Hide workflows' : 'Show workflows'}
              </Button>
            ) : null}
            {!readOnlyOverview ? (
              <Button
                type="button"
                variant="outline"
                onClick={undo}
                disabled={history.current.past.length === 0 && !historyPending.current}
              >
                Undo
              </Button>
            ) : null}
            {!readOnlyOverview ? (
              <Button
                type="button"
                variant="outline"
                onClick={redo}
                disabled={history.current.future.length === 0}
              >
                Redo
              </Button>
            ) : null}
            {!readOnlyOverview && saveError ? (
              <p role="alert" className="text-sm text-destructive">
                {saveError}
              </p>
            ) : null}
            {!readOnlyOverview && canvasEditError ? (
              <p role="alert" className="basis-full text-sm text-destructive">
                {canvasEditError}
              </p>
            ) : null}
            {!readOnlyOverview && !isDirty && saveMessage ? (
              <p className="text-sm text-muted-foreground">{saveMessage}</p>
            ) : null}
            {!readOnlyOverview ? (
              <span className="text-sm text-muted-foreground" aria-live="polite">
                {isDirty ? 'Unsaved changes' : ''}
              </span>
            ) : null}
            {!readOnlyOverview ? (
              <Button type="submit" disabled={save.isPending || createWorkflow.isPending}>
                {save.isPending || createWorkflow.isPending ? 'Saving…' : 'Save workflow'}
              </Button>
            ) : null}
            {!readOnlyOverview ? (
              <Button
                type="button"
                variant="outline"
                onClick={() => (isDirty ? setSaveRunPromptOpen(true) : setRunDialogOpen(true))}
              >
                Run workflow
              </Button>
            ) : null}
          </div>
        ) : null}
        <React.Suspense
          fallback={
            <div
              role="status"
              className="flex min-h-[480px] items-center justify-center rounded-2xl border bg-background text-sm text-muted-foreground"
              style={{ height: 'min(680px, calc(100vh - 260px))' }}
            >
              Loading the workflow picture…
            </div>
          }
        >
          <div
            onBlurCapture={(event) => {
              if (
                event.relatedTarget instanceof Node &&
                event.currentTarget.contains(event.relatedTarget)
              )
                return;
              flushPendingHistory();
            }}
          >
            <WorkflowCanvas
              key={`${workflowId}:${readOnlyOverview ? 'overview' : 'edit'}`}
              steps={steps}
              inputFields={inputFields}
              agents={agents.map((agent) => ({
                id: agent.id,
                name: agent.name,
                icon: agent.persona?.icon,
                color: agent.persona?.color,
              }))}
              selectedKey={selectedCanvasKey}
              governed={workflow.governed}
              showValidation={showValidation}
              openStartWhenEmpty={isNew}
              readOnly={readOnlyOverview}
              onSelect={(key) => {
                setSelectedCanvasKey(key);
                if (!key) setCanvasEditError(null);
              }}
              name={name}
              onNameChange={setName}
              onInputFieldsChange={setInputFields}
              onStepChange={updateStep}
              onAddStep={addCanvasStep}
              onExtendPath={extendCanvasPath}
              onDeleteStep={(nextSteps) => {
                setSteps(nextSteps);
                setSelectedCanvasKey('__workflow_start__');
                setCanvasEditError(null);
              }}
              onEditResult={applyCanvasEdit}
              onAddChoiceRule={(result) => applyCanvasEdit(result, false)}
              onTestStep={async (stepKey, input) => {
                if (isDirty && !(await saveDraft()))
                  return {
                    results: null,
                    error: 'Save the workflow before testing this step.',
                    droppedKeys: [],
                  };
                const node = steps.find((candidate) => candidate.key === stepKey);
                if (!node) return { results: null, error: 'Step not found.', droppedKeys: [] };
                const typedInput = workflowTestInput(input, [
                  ...inputFields,
                  ...steps.flatMap((candidate) => candidate.writes),
                ]);
                return testWorkflowStep(savedWorkflowId.current, stepKey, typedInput);
              }}
            />
          </div>
        </React.Suspense>
        {readOnlyOverview ? <div className="space-y-4">{overviewRuns}</div> : null}
      </form>

      <Dialog open={runDialogOpen} onOpenChange={setRunDialogOpen}>
        <DialogContent className="max-h-[85vh] overflow-y-auto">
          <DialogHeader>
            <DialogTitle>Run workflow</DialogTitle>
            <DialogDescription>
              {inputFields.length
                ? 'Enter the starting information for this run.'
                : 'This workflow needs nothing to start.'}
            </DialogDescription>
          </DialogHeader>
          <form
            ref={runFormRef}
            id="workflow-run-form"
            className="space-y-3"
            onSubmit={(event) => void onRun(event)}
          >
            {inputFields.map((field) => (
              <div key={field.key} className="space-y-1">
                <Label htmlFor={`workflow-run-${field.key}`}>
                  {fieldDisplayName(field)}
                  {field.required ? ' (required)' : ''}
                </Label>
                {field.enumValues?.length ? (
                  <Select
                    id={`workflow-run-${field.key}`}
                    value={runValues[field.name] ?? ''}
                    onChange={(event) => setRunField(field.name, event.target.value)}
                    required={field.required}
                  >
                    <option value="">Select</option>
                    {field.enumValues.map((answer) => (
                      <option key={answer} value={answer}>
                        {answer}
                      </option>
                    ))}
                  </Select>
                ) : field.type === 'boolean' ? (
                  <Select
                    id={`workflow-run-${field.key}`}
                    value={runValues[field.name] ?? ''}
                    onChange={(event) => setRunField(field.name, event.target.value)}
                    required={field.required}
                  >
                    <option value="">Select</option>
                    <option value="true">Yes</option>
                    <option value="false">No</option>
                  </Select>
                ) : (
                  <Input
                    id={`workflow-run-${field.key}`}
                    type={field.type === 'string' ? 'text' : 'number'}
                    step={field.type === 'integer' ? '1' : undefined}
                    value={runValues[field.name] ?? ''}
                    onChange={(event) => setRunField(field.name, event.target.value)}
                    required={field.required}
                  />
                )}
              </div>
            ))}
            <div className="space-y-2">
              <div className="flex flex-wrap items-center gap-2">
                <Button
                  type="button"
                  variant="outline"
                  disabled={start.isPending || runFileUploadPending || !firstStepAgentId}
                  onClick={() => setRunFilePickerOpen(true)}
                >
                  Choose files
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  disabled={start.isPending || runFileUploadPending || !firstStepAgentId}
                  onClick={() => runFileUploadInputRef.current?.click()}
                >
                  {runFileUploadPending ? 'Uploading…' : 'Upload'}
                </Button>
                <input
                  ref={runFileUploadInputRef}
                  type="file"
                  className="sr-only"
                  aria-label="Upload a file"
                  onChange={(event) => void uploadRunFile(event)}
                />
                {firstStepAgentId ? (
                  <p className="text-xs text-muted-foreground">
                    Files come from {firstStepAgent?.name ?? 'the first step agent'}&apos;s
                    workspace. This workflow runs on the server and can only use files stored there.
                  </p>
                ) : (
                  <p className="text-xs text-muted-foreground">Choose a first step agent first.</p>
                )}
              </div>
              {runFilePaths.length > 0 ? (
                <div className="flex flex-wrap gap-2">
                  {runFilePaths.map((path) => (
                    <span
                      key={path}
                      className="flex max-w-full items-center gap-2 rounded-full border border-border/70 bg-background px-3 py-1 text-xs"
                    >
                      <span className="truncate font-medium">{path}</span>
                      <button
                        type="button"
                        className="rounded-full p-0.5 text-muted-foreground hover:bg-muted hover:text-foreground"
                        aria-label={`Remove ${path}`}
                        onClick={() =>
                          setRunFilePaths((current) => current.filter((item) => item !== path))
                        }
                      >
                        <X className="h-3 w-3" />
                      </button>
                    </span>
                  ))}
                </div>
              ) : null}
            </div>
            <WorkspaceFilePickerDialog
              open={runFilePickerOpen}
              onOpenChange={setRunFilePickerOpen}
              agent={filePickerAgent}
              selectedPaths={runFilePaths}
              onSelectPath={selectRunFile}
            />
            <details className="rounded-md border p-3">
              <summary className="cursor-pointer text-sm font-medium">More options</summary>
              <div className="mt-3 space-y-1">
                <Label htmlFor="workflow-run-correlation">
                  Run ID (optional), stops the same job running twice
                </Label>
                <Input
                  id="workflow-run-correlation"
                  value={correlationKey}
                  onChange={(event) => setCorrelationKey(event.target.value)}
                />
              </div>
            </details>
            {runError ? <p className="text-sm text-destructive">{runError}</p> : null}
            <Button type="submit" disabled={start.isPending}>
              {start.isPending ? 'Starting…' : 'Run workflow'}
            </Button>
          </form>
        </DialogContent>
      </Dialog>
      <Dialog open={saveRunPromptOpen} onOpenChange={setSaveRunPromptOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Save changes first?</DialogTitle>
          </DialogHeader>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => setSaveRunPromptOpen(false)}>
              Cancel
            </Button>
            <Button
              type="button"
              onClick={() =>
                void saveDraft().then((saved) => {
                  if (!saved) return;
                  setSaveRunPromptOpen(false);
                  setRunDialogOpen(true);
                })
              }
            >
              Save and run
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
};

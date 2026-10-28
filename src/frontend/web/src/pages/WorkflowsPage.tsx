import { WorkspaceSidebar } from '@/components/layout/WorkspaceSidebar';
import { Button } from '@/components/ui/button';
import { usePendingApprovals } from '@/features/approvals';
import { apiError, useAgents } from '@/features/settings';
import {
  useArchiveWorkflow,
  useDeleteWorkflow,
  useRestoreWorkflow,
  useWorkflowInstances,
  useWorkflows,
  WorkflowCatalogView,
  WorkflowDefinitionView,
  WorkflowInstanceView,
  WorkflowStatusBadge,
  fieldDisplayName,
  workflowRunStatusText,
  type WorkflowDefinition,
} from '@/features/workflows';
import { GitBranch, MoreHorizontal } from 'lucide-react';
import React from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router-dom';

interface WorkflowActionsMenuProps {
  workflowId: string;
  name: string;
  hasRuns: boolean;
  onArchive: (workflowId: string) => Promise<void>;
  onDelete: (workflowId: string) => Promise<void>;
  errorMessage?: string;
  onError: (workflowId: string, message: string) => void;
  onCompleted: (workflowId: string, deleted: boolean) => void;
}

interface WorkflowHistoryState {
  idx?: number;
  workflowDirtyGuard?: boolean;
}

function archivedInstruction(template: string, workflow: WorkflowDefinition): string {
  const schemas = [
    workflow.inputSchema,
    workflow.workingSchema,
    ...workflow.nodes.map((node) => node.outputSchema ?? {}),
  ];
  const labels = new Map<string, string>();
  for (const schema of schemas) {
    const properties = schema.properties;
    if (!properties || typeof properties !== 'object' || Array.isArray(properties)) continue;
    for (const [name, value] of Object.entries(properties as Record<string, unknown>)) {
      const title =
        value && typeof value === 'object' && 'title' in value && typeof value.title === 'string'
          ? value.title
          : undefined;
      labels.set(name, fieldDisplayName({ name, label: title }));
    }
  }
  return template.replace(
    /\{\{\s*([^{}]+?)\s*\}\}/g,
    (_match, name: string) => labels.get(name.trim()) ?? fieldDisplayName({ name: name.trim() })
  );
}

const workflowHistoryState = (state: unknown): WorkflowHistoryState =>
  state && typeof state === 'object' ? (state as WorkflowHistoryState) : {};

const WorkflowActionsMenu: React.FC<WorkflowActionsMenuProps> = ({
  workflowId,
  name,
  hasRuns,
  onArchive,
  onDelete,
  errorMessage,
  onError,
  onCompleted,
}) => {
  const [open, setOpen] = React.useState(false);
  const rootRef = React.useRef<HTMLDivElement>(null);

  React.useEffect(() => {
    if (!open) return;
    const closeOnOutsideClick = (event: MouseEvent) => {
      if (event.target instanceof Node && !rootRef.current?.contains(event.target)) setOpen(false);
    };
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false);
    };
    document.addEventListener('mousedown', closeOnOutsideClick);
    document.addEventListener('keydown', closeOnEscape);
    return () => {
      document.removeEventListener('mousedown', closeOnOutsideClick);
      document.removeEventListener('keydown', closeOnEscape);
    };
  }, [open]);

  const chooseAction = () => {
    setOpen(false);
    const confirmed = window.confirm(
      hasRuns
        ? `Archive '${name}'? It won't run any more, but its past runs stay. You can restore it any time.`
        : `Delete '${name}'? This can't be undone.`
    );
    if (!confirmed) return;
    const action = hasRuns ? onArchive : onDelete;
    void action(workflowId)
      .then(() => {
        onCompleted(workflowId, !hasRuns);
      })
      .catch((error: unknown) => onError(workflowId, apiError(error)));
  };

  return (
    <div ref={rootRef} className="relative" data-workflow-actions>
      <button
        type="button"
        aria-label={`Workflow actions for ${name}`}
        aria-haspopup="menu"
        aria-expanded={open}
        className="flex h-8 w-8 items-center justify-center rounded-md hover:bg-muted"
        onClick={() => setOpen((value) => !value)}
      >
        <MoreHorizontal className="h-4 w-4" />
      </button>
      {open ? (
        <div
          role="menu"
          className="absolute right-0 top-9 z-30 min-w-44 rounded-md border bg-popover p-1 shadow-md"
        >
          <button
            type="button"
            role="menuitem"
            className="w-full rounded px-3 py-2 text-left text-sm hover:bg-muted"
            onClick={chooseAction}
          >
            {hasRuns ? 'Archive workflow' : 'Delete workflow'}
          </button>
        </div>
      ) : null}
      {errorMessage ? (
        <p
          role="alert"
          className="absolute right-0 top-9 z-20 w-64 rounded border bg-background p-2 text-xs text-destructive shadow-md"
        >
          {errorMessage}
        </p>
      ) : null}
    </div>
  );
};

export const WorkflowsPage: React.FC = () => {
  const { workflowId, instanceId } = useParams();
  const location = useLocation();
  const navigate = useNavigate();
  const { data: workflows = [], isLoading, error } = useWorkflows();
  const { data: archivedWorkflows = [] } = useWorkflows(true);
  const { data: pendingApprovals = [] } = usePendingApprovals('mine');
  const { data: agents = [] } = useAgents();
  const { data: instances = [], error: instancesError } = useWorkflowInstances(workflowId);
  const archive = useArchiveWorkflow();
  const remove = useDeleteWorkflow();
  const restore = useRestoreWorkflow();
  const [focusRun, setFocusRun] = React.useState(false);
  const [focusAfterCreateId, setFocusAfterCreateId] = React.useState<string | null>(null);
  const [sidebarOpen, setSidebarOpen] = React.useState(false);
  const [workflowDirty, setWorkflowDirty] = React.useState(false);
  const workflowDirtyRef = React.useRef(false);
  const dirtyHistoryEntry = React.useRef(false);
  const dirtyHistoryIndex = React.useRef<number | null>(null);
  const dirtyHistoryState = React.useRef<unknown>(null);
  const dirtyHistoryHref = React.useRef<string | null>(null);
  workflowDirtyRef.current = workflowDirty;
  const [lifecycleError, setLifecycleError] = React.useState<{
    workflowId: string;
    message: string;
  } | null>(null);
  const isEditing = Boolean(workflowId === 'new' || location.pathname.endsWith('/edit'));
  const isEditingRef = React.useRef(isEditing);
  isEditingRef.current = isEditing;

  React.useEffect(() => {
    if (!focusAfterCreateId || workflowId !== focusAfterCreateId) return;
    let frame = 0;
    const focusSaveButton = () => {
      const target = document.querySelector<HTMLButtonElement>(
        '.workflow-canvas-actions button[type="submit"]'
      );
      if (target && !target.disabled) target.focus();
      else if (frame++ < 4) requestAnimationFrame(focusSaveButton);
    };
    requestAnimationFrame(focusSaveButton);
    setFocusAfterCreateId(null);
  }, [focusAfterCreateId, workflowId]);

  const navigateWithPrompt = (
    path: string,
    state?: { targetType: 'workflow'; targetId: string }
  ) => {
    if (
      isEditingRef.current &&
      workflowDirtyRef.current &&
      !window.confirm('Leave without saving?')
    )
      return;
    setWorkflowDirty(false);
    setSidebarOpen(false);
    navigate(path, state ? { state } : undefined);
  };

  React.useEffect(() => {
    if (!isEditing || !workflowDirty) return;
    if (!dirtyHistoryEntry.current) {
      const currentState = workflowHistoryState(window.history.state);
      const nextState = {
        ...currentState,
        idx: typeof currentState.idx === 'number' ? currentState.idx + 1 : undefined,
        workflowDirtyGuard: true,
      };
      window.history.pushState(nextState, '', window.location.href);
      dirtyHistoryEntry.current = true;
      dirtyHistoryIndex.current = typeof nextState.idx === 'number' ? nextState.idx : null;
      dirtyHistoryState.current = window.history.state;
      dirtyHistoryHref.current = window.location.href;
    }
  }, [isEditing, workflowDirty]);

  React.useEffect(() => {
    const guardPopstate = (event: PopStateEvent) => {
      if (!isEditingRef.current || !workflowDirtyRef.current) return;
      if (!window.confirm('Leave without saving?')) {
        event.stopImmediatePropagation();
        if (dirtyHistoryHref.current) {
          window.history.pushState(dirtyHistoryState.current, '', dirtyHistoryHref.current);
        }
        return;
      }
      const returningToGuardedPage = dirtyHistoryHref.current === window.location.href;
      dirtyHistoryEntry.current = false;
      dirtyHistoryIndex.current = null;
      dirtyHistoryState.current = null;
      dirtyHistoryHref.current = null;
      workflowDirtyRef.current = false;
      setWorkflowDirty(false);
      if (returningToGuardedPage) {
        event.stopImmediatePropagation();
        window.history.go(-1);
      }
    };
    window.addEventListener('popstate', guardPopstate, true);
    return () => window.removeEventListener('popstate', guardPopstate, true);
  }, []);

  React.useEffect(() => {
    const guardBeforeUnload = (event: BeforeUnloadEvent) => {
      if (!isEditingRef.current || !workflowDirtyRef.current) return;
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', guardBeforeUnload);
    return () => window.removeEventListener('beforeunload', guardBeforeUnload);
  }, []);

  React.useEffect(() => {
    if (!isEditing || !workflowDirty) {
      const removeDirtyEntry =
        dirtyHistoryEntry.current &&
        dirtyHistoryHref.current === window.location.href &&
        workflowHistoryState(window.history.state).workflowDirtyGuard === true;
      dirtyHistoryEntry.current = false;
      dirtyHistoryIndex.current = null;
      dirtyHistoryState.current = null;
      dirtyHistoryHref.current = null;
      if (removeDirtyEntry) window.history.go(-1);
      return;
    }
    const guardNavigation = (event: MouseEvent) => {
      if (!(event.target instanceof Element)) return;
      const anchor = event.target.closest<HTMLAnchorElement>('a[href]');
      if (!anchor || anchor.target || anchor.hasAttribute('download')) return;
      const destination = new URL(anchor.href, window.location.href);
      if (destination.origin !== window.location.origin) return;
      if (
        destination.pathname === window.location.pathname &&
        destination.search === window.location.search &&
        destination.hash === window.location.hash
      )
        return;
      event.preventDefault();
      event.stopImmediatePropagation();
      if (window.confirm('Leave without saving?')) {
        setWorkflowDirty(false);
        setSidebarOpen(false);
        navigate(`${destination.pathname}${destination.search}${destination.hash}`);
      }
    };
    document.addEventListener('click', guardNavigation, true);
    return () => document.removeEventListener('click', guardNavigation, true);
  }, [isEditing, navigate, workflowDirty]);

  const toggleSidebar = () => {
    setSidebarOpen((open) => !open);
  };

  const onCreate = () => navigateWithPrompt('/workflows/new');

  const selectedWorkflow = workflows.find((workflow) => workflow.id === workflowId);
  const selectedArchivedWorkflow = archivedWorkflows.find((workflow) => workflow.id === workflowId);
  const isEditorPage = Boolean(
    workflowId && !instanceId && !selectedArchivedWorkflow && (workflowId === 'new' || isEditing)
  );
  React.useEffect(() => {
    if (isEditorPage) setSidebarOpen(false);
  }, [isEditorPage, workflowId]);
  const sidebarVisible = !isEditorPage || sidebarOpen;
  const sidebarItems = workflows.map((workflow) => ({
    id: workflow.id,
    name: workflow.name,
    description: `${workflow.nodes.length} ${workflow.nodes.length === 1 ? 'step' : 'steps'} · Changed ${Number.isNaN(Date.parse(workflow.updatedAt)) ? 'date unavailable' : new Date(workflow.updatedAt).toLocaleDateString()}`,
    color: '#0F766E',
    icon: <GitBranch className="h-4 w-4 text-primary" />,
    actionsOnHover: true,
    actions: (
      <WorkflowActionsMenu
        workflowId={workflow.id}
        name={workflow.name}
        hasRuns={Boolean(workflow.hasRuns)}
        onArchive={(id) => archive.mutateAsync(id)}
        onDelete={(id) => remove.mutateAsync(id)}
        errorMessage={
          lifecycleError?.workflowId === workflow.id ? lifecycleError.message : undefined
        }
        onError={(id, message) => setLifecycleError({ workflowId: id, message })}
        onCompleted={(id, deleted) => {
          setLifecycleError(null);
          if (deleted && workflowId === id) navigate('/workflows');
        }}
      />
    ),
  }));

  const sidebar = (
    <WorkspaceSidebar
      title="Workflows"
      ariaLabel="Workflows and runs"
      addLabel="New workflow"
      selectedId={workflowId}
      onAdd={() => void onCreate()}
      onSelect={(id) => navigateWithPrompt(`/workflows/${id}`)}
      items={sidebarItems}
      emptyText={isLoading ? 'Loading workflows…' : error ? apiError(error) : 'No workflows yet.'}
      footer={
        archivedWorkflows.length ? (
          <details className="mt-4 border-t pt-3">
            <summary className="cursor-pointer px-2.5 py-2 text-sm font-semibold">
              Archived ({archivedWorkflows.length})
            </summary>
            <div className="space-y-1 pl-2">
              {archivedWorkflows.map((workflow) => (
                <button
                  key={workflow.id}
                  type="button"
                  className="block w-full truncate rounded px-2.5 py-2 text-left text-sm hover:bg-muted"
                  onClick={() => navigateWithPrompt(`/workflows/${workflow.id}`)}
                >
                  {workflow.name}
                </button>
              ))}
            </div>
          </details>
        ) : null
      }
      renderChildren={(item) => (
        <>
          <div className="flex items-center justify-between gap-2 px-1 py-1">
            <span className="text-xs font-semibold text-muted-foreground">Runs</span>
            <Button
              type="button"
              variant="ghost"
              size="sm"
              className="h-7 px-2 text-xs"
              onClick={() => {
                setFocusRun(true);
                navigateWithPrompt(`/workflows/${item.id}`);
              }}
            >
              New run
            </Button>
          </div>
          {instancesError ? (
            <p className="px-2.5 py-2 text-xs text-destructive">{apiError(instancesError)}</p>
          ) : null}
          {[...instances]
            .sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt))
            .map((instance) => (
              <Link
                key={instance.id}
                to={`/workflows/${item.id}/instances/${instance.id}`}
                className="flex items-center justify-between gap-2 rounded-md px-2.5 py-2 text-sm hover:bg-muted"
              >
                <span className="min-w-0 flex-1 break-words text-xs">
                  {new Date(instance.createdAt).toLocaleString([], {
                    dateStyle: 'medium',
                    timeStyle: 'short',
                  })}
                </span>
                <span className="shrink-0 whitespace-nowrap">
                  <WorkflowStatusBadge
                    status={instance.status}
                    label={
                      instance.status === 'paused'
                        ? workflowRunStatusText(instance, pendingApprovals)
                        : undefined
                    }
                  />
                </span>
              </Link>
            ))}
          {instances.length === 0 && !instancesError ? (
            <p className="px-2.5 py-2 text-xs text-muted-foreground">No runs yet.</p>
          ) : null}
        </>
      )}
    />
  );

  let content: React.ReactNode;
  if (workflowId && instanceId) {
    content = <WorkflowInstanceView workflowId={workflowId} instanceId={instanceId} />;
  } else if (workflowId) {
    content =
      workflowId === 'new' ? (
        <WorkflowDefinitionView
          workflowId="new"
          isNew
          focusRun={focusRun}
          onRunFocused={() => setFocusRun(false)}
          workflowsListOpen={sidebarOpen}
          onToggleWorkflowsList={toggleSidebar}
          onDirtyChange={setWorkflowDirty}
          onNavigateWithPrompt={navigateWithPrompt}
          onFirstSaveNavigate={(path) => {
            const createdId = /^\/workflows\/([^/]+)\/edit$/.exec(path)?.[1];
            if (createdId) setFocusAfterCreateId(createdId);
            setWorkflowDirty(false);
            workflowDirtyRef.current = false;
            const hadGuard = dirtyHistoryEntry.current;
            dirtyHistoryEntry.current = false;
            dirtyHistoryHref.current = null;
            dirtyHistoryState.current = null;
            dirtyHistoryIndex.current = null;
            if (hadGuard) {
              navigate(path, { replace: true });
              window.addEventListener('popstate', () => navigate(path, { replace: true }), {
                once: true,
              });
              window.history.go(-1);
            } else {
              navigate(path, { replace: true });
            }
          }}
        />
      ) : selectedArchivedWorkflow ? (
        <section
          className="workspace-panel min-h-full space-y-5 p-6"
          aria-label="Archived workflow"
        >
          <header className="flex flex-wrap items-center gap-3 border-b pb-4">
            <h1 className="mr-auto text-xl font-semibold">{selectedArchivedWorkflow.name}</h1>
            <span className="text-sm text-muted-foreground">Archived · read only</span>
            <Button
              type="button"
              onClick={() =>
                void restore
                  .mutateAsync(selectedArchivedWorkflow.id)
                  .then(() => setLifecycleError(null))
                  .catch((error: unknown) =>
                    setLifecycleError({
                      workflowId: selectedArchivedWorkflow.id,
                      message: apiError(error),
                    })
                  )
              }
            >
              Restore
            </Button>
          </header>
          {lifecycleError?.workflowId === selectedArchivedWorkflow.id ? (
            <p role="alert" className="text-sm text-destructive">
              {lifecycleError.message}
            </p>
          ) : null}
          <div className="space-y-2">
            <h2 className="font-semibold">Steps</h2>
            {selectedArchivedWorkflow.nodes.map((node, index) => (
              <article key={node.nodeId} className="rounded-xl border bg-card p-4 shadow-sm">
                <div className="flex items-center gap-2">
                  <span className="rounded-full bg-muted px-2 py-0.5 text-xs font-semibold text-muted-foreground">
                    Step {index + 1}
                  </span>
                  <span className="font-semibold">
                    {agents.find((agent) => agent.id === node.agentId)?.name ?? 'Agent removed'}
                  </span>
                  {node.requiresReview ? (
                    <span
                      aria-label="Check with me first"
                      title="Check with me first"
                      className="text-sm text-muted-foreground"
                    >
                      ✓
                    </span>
                  ) : null}
                </div>
                <p className="mt-3 whitespace-pre-wrap text-sm">
                  {archivedInstruction(node.promptTemplate, selectedArchivedWorkflow) ||
                    'No instruction yet.'}
                </p>
                {(() => {
                  const properties = (node.outputSchema?.properties ?? {}) as Record<
                    string,
                    { title?: unknown }
                  >;
                  const resultNames = Object.entries(properties).map(([key, schema]) =>
                    fieldDisplayName({
                      name: key,
                      label: typeof schema?.title === 'string' ? schema.title : undefined,
                    })
                  );
                  return resultNames.length ? (
                    <div className="mt-3 flex flex-wrap gap-1.5">
                      {resultNames.map((result) => (
                        <span
                          key={result}
                          className="rounded-full bg-muted px-2 py-0.5 text-xs text-muted-foreground"
                        >
                          {result}
                        </span>
                      ))}
                    </div>
                  ) : null;
                })()}
              </article>
            ))}
          </div>
          <div className="space-y-2">
            <h2 className="font-semibold">Runs</h2>
            {instancesError ? (
              <p className="text-sm text-destructive">{apiError(instancesError)}</p>
            ) : null}
            {instances.map((run) => (
              <Link
                key={run.id}
                to={`/workflows/${selectedArchivedWorkflow.id}/instances/${run.id}`}
                className="flex items-center justify-between rounded-md border p-3 text-sm hover:bg-muted"
              >
                <span className="min-w-0 flex-1 break-words text-sm">
                  {new Date(run.createdAt).toLocaleString([], {
                    dateStyle: 'medium',
                    timeStyle: 'short',
                  })}
                </span>
                <span className="shrink-0 whitespace-nowrap">
                  <WorkflowStatusBadge
                    status={run.status}
                    label={
                      run.status === 'paused'
                        ? workflowRunStatusText(run, pendingApprovals)
                        : undefined
                    }
                  />
                </span>
              </Link>
            ))}
            {!instances.length && !instancesError ? (
              <p className="text-sm text-muted-foreground">No runs yet.</p>
            ) : null}
          </div>
        </section>
      ) : selectedWorkflow || (workflowId !== 'new' && !selectedArchivedWorkflow) ? (
        <WorkflowDefinitionView
          workflowId={workflowId}
          focusRun={focusRun}
          workflowActions={sidebarItems.find((item) => item.id === workflowId)?.actions}
          onRunFocused={() => setFocusRun(false)}
          workflowsListOpen={sidebarOpen}
          onToggleWorkflowsList={toggleSidebar}
          onDirtyChange={setWorkflowDirty}
          onNavigateWithPrompt={navigateWithPrompt}
          readOnlyOverview={!isEditing}
          overviewRuns={
            !isEditing ? (
              <section className="space-y-3" aria-label="Workflow runs">
                <h2 className="text-base font-semibold">Runs</h2>
                {instancesError ? (
                  <p className="text-sm text-destructive">{apiError(instancesError)}</p>
                ) : null}
                {instances.length ? (
                  <>
                    <div className="text-sm">
                      Last run:{' '}
                      <Link
                        className="underline"
                        to={`/workflows/${workflowId}/instances/${[...instances].sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt))[0]!.id}`}
                      >
                        <WorkflowStatusBadge
                          status={
                            [...instances].sort(
                              (a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt)
                            )[0]!.status
                          }
                          label={
                            [...instances].sort(
                              (a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt)
                            )[0]!.status === 'paused'
                              ? workflowRunStatusText(
                                  [...instances].sort(
                                    (a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt)
                                  )[0]!,
                                  pendingApprovals
                                )
                              : undefined
                          }
                        />
                      </Link>
                    </div>
                    <div className="space-y-2">
                      {[...instances]
                        .sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt))
                        .slice(1, 5)
                        .map((run) => (
                          <Link
                            key={run.id}
                            to={`/workflows/${workflowId}/instances/${run.id}`}
                            className="flex items-center justify-between rounded-md border p-3 text-sm hover:bg-muted"
                          >
                            <span className="min-w-0 flex-1 break-words">
                              {new Date(run.createdAt).toLocaleString([], {
                                dateStyle: 'medium',
                                timeStyle: 'short',
                              })}
                            </span>
                            <span className="shrink-0 whitespace-nowrap">
                              <WorkflowStatusBadge
                                status={run.status}
                                label={
                                  run.status === 'paused'
                                    ? workflowRunStatusText(run, pendingApprovals)
                                    : undefined
                                }
                              />
                            </span>
                          </Link>
                        ))}
                    </div>
                  </>
                ) : !instancesError ? (
                  <p className="text-sm text-muted-foreground">No runs yet.</p>
                ) : null}
              </section>
            ) : null
          }
        />
      ) : (
        <div className="workspace-panel p-6 text-sm text-muted-foreground">
          {isLoading ? 'Loading workflow…' : error ? apiError(error) : 'Workflow not found.'}
        </div>
      );
  } else {
    content = <WorkflowCatalogView compact />;
  }

  return (
    <div className="page-shell-fill max-w-none px-0 sm:px-2">
      <div
        className={`workspace-grid-fill grid-rows-[auto_minmax(0,1fr)] grid-cols-1 gap-3 lg:grid-rows-1 ${sidebarVisible ? 'lg:grid-cols-[21rem_minmax(0,1fr)]' : 'lg:grid-cols-1'}`}
      >
        {sidebarVisible ? sidebar : null}
        <main className="flex min-h-0 min-w-0 flex-col overflow-y-auto">{content}</main>
      </div>
    </div>
  );
};

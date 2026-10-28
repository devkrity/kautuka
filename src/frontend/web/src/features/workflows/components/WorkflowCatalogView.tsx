import { Alert } from '@/components/ui/alert';
import { usePendingApprovals } from '@/features/approvals';
import { Button } from '@/components/ui/button';
import { PageIntro } from '@/components/layout/PageIntro';
import { PageState } from '@/components/layout/PageState';
import { apiError, useAgents } from '@/features/settings';
import { useConnectors } from '@/features/connectors';
import { GitBranch, Plus, Settings } from 'lucide-react';
import React from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useCreateWorkflow, useWorkflows, useWorkflowInstances } from '../api/queries';
import { buildWorkflowTemplate, workflowTemplates } from '../model/workflowTemplates';
import { WorkflowSettingsDialog } from './WorkflowSettingsDialog';
import { WorkflowStatusBadge } from './WorkflowStatusBadge';
import { workflowRunStatusText } from '../model/workflowLabels';

export const WorkflowCatalogView: React.FC<{ compact?: boolean }> = ({ compact = false }) => {
  const navigate = useNavigate();
  const { data: workflows = [], isLoading, error } = useWorkflows();
  const { data: instances = [], error: instancesError } = useWorkflowInstances();
  const { data: pendingApprovals = [] } = usePendingApprovals('mine');
  const { data: agents = [], isLoading: agentsLoading } = useAgents();
  const { data: connectors = [] } = useConnectors();
  const emailAgents = agents.filter((agent) => {
    const bindings =
      (agent as typeof agent & { mcpBindings?: Array<{ connectorId?: string }> }).mcpBindings ?? [];
    return bindings.some((binding) =>
      connectors.some(
        (connector) =>
          connector.enabled &&
          connector.id === binding.connectorId &&
          (connector.id.endsWith('kautuka-m365-mail') ||
            connector.id.endsWith('kautuka-google-mail'))
      )
    );
  });
  const create = useCreateWorkflow();
  const [formError, setFormError] = React.useState<string | null>(null);
  const [settingsOpen, setSettingsOpen] = React.useState(false);

  const onCreateTemplate = async (templateId: string) => {
    setFormError(null);
    if (templateId === 'email-attachments' && emailAgents.length === 0) {
      setFormError('Connect Outlook or Gmail to an agent before adding this workflow.');
      return;
    }
    const agentIds = (templateId === 'email-attachments' ? emailAgents : agents).map(
      (agent) => agent.id
    );
    if (agentIds.length === 0) {
      setFormError('Create an agent from the workspace first, then come back to add a workflow.');
      return;
    }

    try {
      const created = await create.mutateAsync(buildWorkflowTemplate(templateId, agentIds));
      navigate(`/workflows/${created.id}`);
    } catch (createError) {
      setFormError(apiError(createError));
    }
  };

  const onCreate = () => {
    setFormError(null);
    navigate('/workflows/new');
  };

  return (
    <div className="page-shell gap-6">
      <PageIntro
        kicker="Workflows"
        title="Workflows"
        description="Jobs your agents run step by step. You approve the steps you choose."
        actions={
          <div className="flex gap-2">
            <Button variant="outline" onClick={() => setSettingsOpen(true)}>
              <Settings className="h-4 w-4" />
              Settings
            </Button>
            <Button onClick={() => void onCreate()} disabled={create.isPending}>
              <Plus className="h-4 w-4" />
              {create.isPending ? 'Creating…' : 'New workflow'}
            </Button>
          </div>
        }
      />

      {error ? (
        <Alert variant="destructive" className="p-2">
          {apiError(error)}
        </Alert>
      ) : null}
      {formError ? (
        <Alert variant="destructive" className="p-2">
          {formError}
        </Alert>
      ) : null}

      <section className="workspace-panel space-y-3">
        <h2 className="text-sm font-semibold">Start from a business template</h2>
        <div className="grid gap-3 md:grid-cols-2">
          {workflowTemplates.map((template) => (
            <div key={template.id}>
              <button
                type="button"
                onClick={() => void onCreateTemplate(template.id)}
                disabled={create.isPending || agentsLoading}
                className="rounded-xl border border-border bg-background/70 px-4 py-3 text-left hover:border-primary/40 disabled:opacity-50"
              >
                <span className="font-medium">{template.name}</span>
                <p className="mt-1 text-xs text-muted-foreground">{template.blurb}</p>
              </button>
              {template.id === 'email-attachments' && emailAgents.length === 0 ? (
                <p className="px-4 pt-2 text-xs text-muted-foreground">
                  The first step&apos;s agent needs an email connector (Outlook or Gmail) to read
                  email attachments.{' '}
                  <Link className="underline" to="/settings?tab=agents">
                    Open agent connectors
                  </Link>
                </p>
              ) : null}
            </div>
          ))}
        </div>
      </section>

      {!compact ? (
        <div className="grid gap-6 lg:grid-cols-[minmax(0,1.4fr)_minmax(20rem,0.9fr)]">
          <section className="workspace-panel space-y-3">
            <h2 className="text-sm font-semibold">Your workflows</h2>
            {isLoading ? <PageState message="Loading workflows…" /> : null}
            {!isLoading && workflows.length === 0 ? (
              <p className="text-sm text-muted-foreground">
                None yet. New workflow opens an editor where you add steps.
              </p>
            ) : null}
            <div className="space-y-2">
              {workflows.map((workflow) => (
                <Link
                  key={workflow.id}
                  to={`/workflows/${workflow.id}`}
                  className="flex items-center justify-between rounded-xl border border-border bg-background/70 px-4 py-3 hover:border-primary/40"
                >
                  <span className="flex items-center gap-2 font-medium">
                    <GitBranch className="h-4 w-4 text-primary" />
                    {workflow.name}
                  </span>
                  <span className="text-xs text-muted-foreground">
                    {workflow.nodes.length} step{workflow.nodes.length === 1 ? '' : 's'}
                  </span>
                </Link>
              ))}
            </div>
          </section>

          <section className="workspace-panel space-y-3">
            <h2 className="text-sm font-semibold">Recent runs</h2>
            {instancesError ? (
              <p className="text-sm text-destructive">{apiError(instancesError)}</p>
            ) : null}
            {instances.length === 0 && !instancesError ? (
              <p className="text-sm text-muted-foreground">
                Runs show up here after you start a workflow.
              </p>
            ) : (
              <div className="space-y-2">
                {instances.slice(0, 8).map((instance) => (
                  <Link
                    key={instance.id}
                    to={`/workflows/${instance.workflowId}/instances/${instance.id}`}
                    className="flex items-center justify-between rounded-xl border border-border px-3 py-2 text-sm hover:border-primary/40"
                  >
                    <span className="truncate">
                      {String(
                        instance.envelope.title ?? instance.snapshot.name ?? 'Untitled workflow'
                      )}
                    </span>
                    <WorkflowStatusBadge
                      status={instance.status}
                      label={
                        instance.status === 'paused'
                          ? workflowRunStatusText(instance, pendingApprovals)
                          : undefined
                      }
                    />
                  </Link>
                ))}
              </div>
            )}
          </section>
        </div>
      ) : null}
      <WorkflowSettingsDialog open={settingsOpen} onClose={() => setSettingsOpen(false)} />
    </div>
  );
};

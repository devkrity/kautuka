import { AddEntityTile } from '@/components/layout/AddEntityTile';
import { PageState } from '@/components/layout/PageState';
import { StatusPanels } from '@/components/layout/StatusPanels';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Bot } from 'lucide-react';
import React from 'react';
import { useAgentToolPolicy } from '../api/queries';
import { useAgentsTab } from '../hooks/useAgentsTab';
import type { AgentSettingsEntry } from '../types/settingsTypes';
import { AgentForm } from './AgentForm';
import { AgentRevisionHistory } from './AgentRevisionHistory';
import { ConfirmDeleteButton } from './SettingsShared';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { useProgressiveLimit } from '@/components/layout/useProgressiveLimit';
import { ShowMoreButton } from '@/components/layout/ShowMoreButton';

function getAgentConnectorIds(agent: AgentSettingsEntry): string[] {
  return (agent.mcpBindings ?? [])
    .map((binding) => binding.connectorId)
    .filter((connectorId, index, allIds) => allIds.indexOf(connectorId) === index);
}

type AgentsTabProps = {
  requestedAgentId?: string | null;
  createRequested?: boolean;
  onRequestedActionHandled?: () => void;
};

export function AgentsTab({
  requestedAgentId = null,
  createRequested = false,
  onRequestedActionHandled,
}: Readonly<AgentsTabProps>) {
  const {
    agents,
    models,
    mcpServers,
    loading,
    editingId,
    savingId,
    draftAgent,
    personaDefinitions,
    organizationDefaults,
    platformRuntime,
    patchAgent,
    getConnectorMode,
    setConnectorMode,
    getToolMode,
    setToolMode,
    toggleToolPanel,
    isToolPanelOpen,
    saveAgent,
    removeAgent,
    startCreate,
    startEdit,
    cancelEdit,
    statusItems,
    skills,
    getAgentToolMode,
    setAgentToolMode,
  } = useAgentsTab();

  const handledRequest = React.useRef<string | null>(null);
  React.useEffect(() => {
    if (loading) {
      return;
    }

    const requestKey = createRequested
      ? 'create'
      : requestedAgentId
        ? `edit:${requestedAgentId}`
        : null;
    if (requestKey === null) {
      handledRequest.current = null;
      return;
    }
    if (handledRequest.current === requestKey) {
      return;
    }

    handledRequest.current = requestKey;
    if (createRequested) {
      startCreate();
    } else if (requestedAgentId && agents.some((agent) => agent.id === requestedAgentId)) {
      startEdit(requestedAgentId);
    }
    onRequestedActionHandled?.();
  }, [
    agents,
    createRequested,
    loading,
    onRequestedActionHandled,
    requestedAgentId,
    startCreate,
    startEdit,
  ]);

  const activePolicyAgentId = React.useMemo(() => {
    if (editingId === '' && draftAgent) {
      return draftAgent.id || null;
    }

    if (!editingId) {
      return null;
    }

    const editingAgent = agents.find((agent) => agent.id === editingId);
    return editingAgent?.id || null;
  }, [agents, draftAgent, editingId]);

  const { data: activeAgentToolPolicy } = useAgentToolPolicy(activePolicyAgentId);
  const { visibleItems: visibleAgents, hiddenCount, showMore } = useProgressiveLimit(agents);

  const runAsync = (action: () => Promise<unknown>) => {
    action().catch((error) => {
      console.error('Agents action failed:', error);
    });
  };

  const editingAgent =
    editingId === '' ? draftAgent : (agents.find((agent) => agent.id === editingId) ?? null);
  let content: React.ReactNode;
  if (loading) {
    content = <PageState message="Loading agents..." minHeightClassName="min-h-36" />;
  } else {
    content = (
      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
        <AddEntityTile
          title="Add Agent"
          description="Create a new agent with its own model, instructions, and tool access."
          onClick={startCreate}
        />
        {visibleAgents.map((agent) => {
          const connectedMcpIds = getAgentConnectorIds(agent);
          return (
            <Card key={agent.id} className="card-interactive">
              <CardHeader>
                <div className="flex items-start justify-between gap-3">
                  <div className="min-w-0">
                    <div className="mb-2 flex flex-wrap items-center gap-2">
                      <Bot className="h-4 w-4 text-primary" />
                      <Badge variant={agent.superExecMode !== 'disabled' ? 'success' : 'muted'}>
                        {agent.superExecMode !== 'disabled' ? 'SuperExec on' : 'SuperExec off'}
                      </Badge>
                      <Badge variant={agent.sandbox.enabled ? 'info' : 'muted'}>
                        {agent.sandbox.enabled ? 'Sandbox on' : 'Sandbox off'}
                      </Badge>
                      <Badge variant="muted">{agent.scope === 'system' ? 'System' : 'User'}</Badge>
                      {typeof agent.revision === 'number' && agent.revision > 0 ? (
                        <Badge variant="muted">Revision {agent.revision}</Badge>
                      ) : null}
                    </div>
                    <CardTitle>{agent.name || agent.id}</CardTitle>
                    <CardDescription>{agent.id}</CardDescription>
                  </div>
                  <div className="flex shrink-0 gap-2">
                    <Button variant="secondary" onClick={() => startEdit(agent.id)}>
                      Configure
                    </Button>
                    <ConfirmDeleteButton
                      title={`Delete agent ${agent.id}?`}
                      description="This action is blocked if other runtime records still reference this agent."
                      onConfirm={() => runAsync(() => removeAgent(agent.id))}
                    />
                  </div>
                </div>
              </CardHeader>
              <CardContent className="space-y-1 pt-0 text-sm text-muted-foreground">
                <div className="mb-2 flex flex-wrap gap-2">
                  <Badge variant="muted">Model {agent.modelId}</Badge>
                  <Badge variant="muted">Retain {agent.session.maxHistoryRuns} sessions</Badge>
                  {connectedMcpIds.length > 0 && (
                    <Badge variant="info">MCP {connectedMcpIds.length}</Badge>
                  )}
                  {(agent.skillAttachments?.filter((entry) => entry.mode !== 'disabled').length ??
                    0) > 0 && (
                    <Badge variant="info">
                      Skills{' '}
                      {agent.skillAttachments.filter((entry) => entry.mode !== 'disabled').length}
                    </Badge>
                  )}
                </div>
                <p>Model: {agent.modelId}</p>
                <p>
                  SuperExec: {agent.superExecMode}; sandbox:{' '}
                  {agent.sandbox.enabled ? 'enabled' : 'disabled'}; retained sessions:{' '}
                  {agent.session.maxHistoryRuns}
                </p>
                {(agent.skillAttachments?.filter((entry) => entry.mode !== 'disabled').length ??
                  0) > 0 && (
                  <p className="break-words">
                    Skills:{' '}
                    {agent.skillAttachments
                      .filter((entry) => entry.mode !== 'disabled')
                      .map((entry) => `${entry.skillId} (${entry.mode})`)
                      .join(', ')}
                  </p>
                )}
                {connectedMcpIds.length > 0 && (
                  <p className="break-words">MCP connectors: {connectedMcpIds.join(', ')}</p>
                )}
              </CardContent>
            </Card>
          );
        })}
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <Dialog
        open={editingId !== null}
        onOpenChange={(open) => {
          if (!open) cancelEdit();
        }}
      >
        <DialogContent className="flex h-[88vh] w-[96vw] max-w-5xl flex-col overflow-hidden p-0">
          <DialogHeader className="border-b border-border/60 px-5 py-4 pr-12 sm:px-6">
            <DialogTitle>
              {editingId === ''
                ? 'Create agent'
                : `Configure ${editingAgent?.name || editingAgent?.id || 'agent'}`}
            </DialogTitle>
            <DialogDescription>
              Set the agent’s behavior, tools, memory, and runtime access.
            </DialogDescription>
          </DialogHeader>
          {editingAgent ? (
            <div className="min-h-0 flex-1 overflow-y-auto px-4 py-4 sm:px-6">
              <AgentForm
                agent={editingAgent}
                agents={agents}
                models={models}
                personaDefinitions={personaDefinitions}
                organizationDefaults={organizationDefaults}
                platformRuntime={platformRuntime}
                toolPolicyProjection={activeAgentToolPolicy ?? null}
                mcpServers={mcpServers}
                skills={skills}
                getAgentToolMode={(targetAgentId) => getAgentToolMode(editingAgent, targetAgentId)}
                onAgentToolModeChange={(targetAgentId, mode) =>
                  setAgentToolMode(editingId ?? '', targetAgentId, mode)
                }
                isToolPanelOpen={(connectorId) => isToolPanelOpen(editingId ?? '', connectorId)}
                getConnectorMode={(connectorId) => getConnectorMode(editingAgent, connectorId)}
                getToolMode={(connectorId, toolName) =>
                  getToolMode(editingAgent, connectorId, toolName)
                }
                onPatch={(updater) => patchAgent(editingId ?? '', updater)}
                onModeChange={(connectorId, mode) =>
                  setConnectorMode(editingId ?? '', connectorId, mode)
                }
                onTogglePanel={(connectorId) => toggleToolPanel(editingId ?? '', connectorId)}
                onToolModeChange={(connectorId, toolName, mode) =>
                  setToolMode(editingId ?? '', connectorId, toolName, mode)
                }
              />
              {editingId !== '' ? <AgentRevisionHistory agentId={editingAgent.id} /> : null}
            </div>
          ) : null}
          {editingAgent ? (
            <DialogFooter className="border-t border-border/60 px-5 py-4 sm:justify-between sm:px-6">
              {editingId !== '' ? (
                <ConfirmDeleteButton
                  title={`Delete agent ${editingAgent.id}?`}
                  description="This action is blocked if other runtime records still reference this agent."
                  onConfirm={() => runAsync(() => removeAgent(editingAgent.id))}
                />
              ) : (
                <span />
              )}
              <div className="flex justify-end gap-2">
                <Button variant="outline" onClick={cancelEdit}>
                  Cancel
                </Button>
                <Button
                  onClick={() => runAsync(() => saveAgent(editingAgent))}
                  disabled={savingId !== null}
                >
                  {savingId === null ? 'Save changes' : 'Saving…'}
                </Button>
              </div>
            </DialogFooter>
          ) : null}
        </DialogContent>
      </Dialog>

      {content}
      {!loading && <ShowMoreButton count={hiddenCount} onClick={showMore} />}

      <StatusPanels items={statusItems} />
    </div>
  );
}

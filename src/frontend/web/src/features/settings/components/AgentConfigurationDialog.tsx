import { PageState } from '@/components/layout/PageState';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import React from 'react';
import { useAgentToolPolicy } from '../api/queries';
import { useAgentsTab } from '../hooks/useAgentsTab';
import { ROLE_ADMIN, type AgentSettingsEntry } from '../types/settingsTypes';
import { AgentForm } from './AgentForm';
import { AgentRevisionHistory } from './AgentRevisionHistory';
import { ConfirmDeleteButton } from './SettingsShared';

type AgentConfigurationActor = {
  id: string;
  role: string;
};

type AgentConfigurationDialogProps = {
  open: boolean;
  actor: AgentConfigurationActor;
  onSaved: (agentId: string) => void;
  onDeleted: (agentId: string) => void;
  onClose: () => void;
} & ({ mode: 'create'; agentId?: never } | { mode: 'edit'; agentId: string });

function canManageAgent(actor: AgentConfigurationActor, agent: AgentSettingsEntry): boolean {
  return agent.scope === 'system' ? actor.role === ROLE_ADMIN : actor.id === agent.ownerUserId;
}

export function AgentConfigurationDialog({
  open,
  actor,
  mode,
  onSaved,
  onDeleted,
  onClose,
  ...modeProps
}: Readonly<AgentConfigurationDialogProps>) {
  const {
    agents,
    models,
    mcpServers,
    skills,
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
    getAgentToolMode,
    setAgentToolMode,
  } = useAgentsTab();

  const requestedAgentId = mode === 'edit' ? modeProps.agentId : null;
  const requestKey = open ? (mode === 'create' ? 'create' : `edit:${requestedAgentId}`) : null;
  const handledRequest = React.useRef<string | null>(null);

  React.useEffect(() => {
    if (!open) {
      handledRequest.current = null;
      return;
    }
    if (loading || requestKey === null || handledRequest.current === requestKey) {
      return;
    }

    handledRequest.current = requestKey;
    if (mode === 'create') {
      startCreate();
    } else if (requestedAgentId && agents.some((agent) => agent.id === requestedAgentId)) {
      startEdit(requestedAgentId);
    }
  }, [agents, loading, mode, open, requestKey, requestedAgentId, startCreate, startEdit]);

  const configuredAgent = React.useMemo(() => {
    if (editingId === '') {
      return draftAgent;
    }
    if (!editingId) {
      return null;
    }
    return agents.find((agent) => agent.id === editingId) ?? null;
  }, [agents, draftAgent, editingId]);

  const activePolicyAgentId = configuredAgent?.id || null;
  const { data: activeAgentToolPolicy } = useAgentToolPolicy(activePolicyAgentId);

  const close = React.useCallback(() => {
    cancelEdit();
    onClose();
  }, [cancelEdit, onClose]);

  const handleSave = async () => {
    if (!configuredAgent) {
      return;
    }
    const savedAgentId = await saveAgent(configuredAgent);
    if (savedAgentId) {
      onSaved(savedAgentId);
      onClose();
    }
  };

  const handleDelete = async () => {
    if (!configuredAgent || !(await removeAgent(configuredAgent.id))) {
      return;
    }
    onDeleted(configuredAgent.id);
    onClose();
  };

  const isMissingRequestedAgent =
    open &&
    !loading &&
    mode === 'edit' &&
    requestedAgentId !== null &&
    !agents.some((agent) => agent.id === requestedAgentId);

  return (
    <Dialog open={open} onOpenChange={(nextOpen) => !nextOpen && close()}>
      <DialogContent className="flex h-[85vh] w-[95vw] max-w-4xl flex-col overflow-hidden p-0">
        <DialogHeader className="border-b border-border/50 px-6 py-4 pr-10">
          <DialogTitle>
            {mode === 'create'
              ? 'New Agent'
              : configuredAgent?.revision
                ? `Configure Agent · Revision ${configuredAgent.revision}`
                : 'Configure Agent'}
          </DialogTitle>
        </DialogHeader>

        {loading ? (
          <PageState message="Loading agent configuration..." />
        ) : isMissingRequestedAgent ? (
          <PageState message="Agent not found." />
        ) : configuredAgent ? (
          <>
            <div className="min-h-0 flex-1 overflow-y-auto px-6 py-4">
              <AgentForm
                agent={configuredAgent}
                agents={agents}
                models={models}
                mcpServers={mcpServers}
                skills={skills}
                personaDefinitions={personaDefinitions}
                organizationDefaults={organizationDefaults}
                platformRuntime={platformRuntime}
                toolPolicyProjection={activeAgentToolPolicy ?? null}
                onPatch={(updater) => patchAgent(configuredAgent.id, updater)}
                getAgentToolMode={(targetAgentId) =>
                  getAgentToolMode(configuredAgent, targetAgentId)
                }
                onAgentToolModeChange={(targetAgentId, toolMode) =>
                  setAgentToolMode(configuredAgent.id, targetAgentId, toolMode)
                }
                getConnectorMode={(connectorId) => getConnectorMode(configuredAgent, connectorId)}
                onModeChange={(connectorId, connectorMode) =>
                  setConnectorMode(configuredAgent.id, connectorId, connectorMode)
                }
                getToolMode={(connectorId, toolName) =>
                  getToolMode(configuredAgent, connectorId, toolName)
                }
                onToolModeChange={(connectorId, toolName, toolMode) =>
                  setToolMode(configuredAgent.id, connectorId, toolName, toolMode)
                }
                onTogglePanel={(connectorId) => toggleToolPanel(configuredAgent.id, connectorId)}
                isToolPanelOpen={(connectorId) => isToolPanelOpen(configuredAgent.id, connectorId)}
              />
              {mode === 'edit' ? <AgentRevisionHistory agentId={configuredAgent.id} /> : null}
            </div>
            <DialogFooter className="border-t border-border/50 px-6 py-4 sm:justify-between">
              {mode === 'edit' && canManageAgent(actor, configuredAgent) ? (
                <ConfirmDeleteButton
                  title={`Delete agent ${configuredAgent.id}?`}
                  description="This action is blocked if other runtime records still reference this agent."
                  onConfirm={() => void handleDelete()}
                />
              ) : (
                <span />
              )}
              <div className="flex justify-end gap-2">
                <Button type="button" variant="secondary" onClick={close}>
                  Cancel
                </Button>
                {mode === 'create' || canManageAgent(actor, configuredAgent) ? (
                  <Button
                    type="button"
                    onClick={() => void handleSave()}
                    disabled={savingId !== null}
                  >
                    {savingId === null ? 'Save' : 'Saving...'}
                  </Button>
                ) : null}
              </div>
            </DialogFooter>
          </>
        ) : null}
      </DialogContent>
    </Dialog>
  );
}

import { Alert } from '@/components/ui/alert';
import { SegmentedControl } from '@/components/ui/segmented-control';
import { TabsContent } from '@/components/ui/tabs';
import type { Connector } from '@/features/connectors';
import type { AgentToolMode } from '../hooks/useAgentsTab';
import type { AgentSettingsEntry } from '../types/settingsTypes';
import { McpPanel } from './shared/McpPanel';

const bundleExecutionContextOptions = [
  { value: 'OwnContext', label: 'Isolated' },
  { value: 'OwnContextWithAgentFiles', label: 'Isolated with access to Agent Files' },
  { value: 'AgentContext', label: 'Within Agent Sandbox' },
] as const;
const agentSandboxExecutionContext = 'AgentContext';
const defaultBundleExecutionContext = 'OwnContext';

interface AgentConnectorsEditorProps {
  agent: AgentSettingsEntry;
  agents: AgentSettingsEntry[];
  connectors: Connector[];
  selectionEnabled: boolean;
  selectionMessage: string;
  isToolPanelOpen: (connectorId: string) => boolean;
  getConnectorMode: (connectorId: string) => 'off' | 'on' | 'advanced';
  getToolMode: (connectorId: string, toolName: string) => 'off' | 'on' | 'ask';
  getAgentToolMode: (targetAgentId: string) => AgentToolMode;
  onPatch: (updater: (previous: AgentSettingsEntry) => AgentSettingsEntry) => void;
  onModeChange: (connectorId: string, mode: 'off' | 'on' | 'advanced') => void;
  onTogglePanel: (connectorId: string) => void;
  onToolModeChange: (connectorId: string, toolName: string, mode: 'off' | 'on' | 'ask') => void;
  onAgentToolModeChange: (targetAgentId: string, mode: AgentToolMode) => void;
}

export function AgentConnectorsEditor({
  agent,
  agents,
  connectors,
  selectionEnabled,
  selectionMessage,
  isToolPanelOpen,
  getConnectorMode,
  getToolMode,
  getAgentToolMode,
  onPatch,
  onModeChange,
  onTogglePanel,
  onToolModeChange,
  onAgentToolModeChange,
}: Readonly<AgentConnectorsEditorProps>) {
  const sandboxEnabled = agent.sandbox.enabled;
  const agentToolSelectionLocked = agent.toolExposure.exposedAsTool;
  const otherAgents = agents.filter((candidate) => candidate.id !== agent.id);
  const groups = {
    remote: connectors.filter((connector) => connector.transport === 'http'),
    bundles: connectors.filter((connector) =>
      Boolean(connector.bundlePath || connector.serverType)
    ),
    containers: connectors.filter(
      (connector) =>
        connector.transport !== 'http' && !connector.bundlePath && !connector.serverType
    ),
  };

  const getBinding = (connectorId: string) =>
    agent.mcpBindings.find((binding) => binding.connectorId === connectorId) ?? null;
  const getBundleExecutionContext = (connectorId: string): string =>
    !sandboxEnabled && getBinding(connectorId)?.context === agentSandboxExecutionContext
      ? defaultBundleExecutionContext
      : (getBinding(connectorId)?.context ?? defaultBundleExecutionContext);

  const upsertBinding = (connectorId: string, context: string) => {
    onPatch((previous) => {
      const bindings = previous.mcpBindings
        .filter((binding) => binding.connectorId.trim().length > 0)
        .filter(
          (binding, index, all) =>
            all.findIndex((candidate) => candidate.connectorId === binding.connectorId) === index
        );
      const nextBindings = bindings.some((binding) => binding.connectorId === connectorId)
        ? bindings.map((binding) =>
            binding.connectorId === connectorId ? { ...binding, context } : binding
          )
        : [...bindings, { connectorId, context }];
      return {
        ...previous,
        mcpBindings: nextBindings,
      };
    });
  };

  const renderSection = (
    title: string,
    description: string,
    sectionConnectors: Connector[],
    renderPanelFooter?: (connector: Connector) => React.ReactNode
  ) =>
    sectionConnectors.length === 0 ? null : (
      <div className="settings-subpanel-compact">
        <div className="mb-3 flex items-start justify-between gap-3">
          <div>
            <div className="text-sm font-medium">{title}</div>
            <p className="mt-1 text-xs text-muted-foreground">{description}</p>
          </div>
          {!selectionEnabled ? (
            <div className="text-xs text-muted-foreground">{selectionMessage}</div>
          ) : null}
        </div>
        <div className="space-y-3">
          {sectionConnectors.map((connector) => {
            const connectorId = String(connector.id);
            return (
              <div key={`${agent.id}-${connectorId}-section`}>
                <McpPanel
                  ownerId={agent.id}
                  connector={connector}
                  mode={getConnectorMode(connectorId)}
                  selectionEnabled={selectionEnabled}
                  isPanelOpen={isToolPanelOpen(connectorId)}
                  onModeChange={(mode) => onModeChange(connectorId, mode)}
                  onTogglePanel={() => onTogglePanel(connectorId)}
                  getToolMode={(toolName) => getToolMode(connectorId, toolName)}
                  onToolModeChange={(toolName, mode) =>
                    onToolModeChange(connectorId, toolName, mode)
                  }
                  footer={renderPanelFooter?.(connector)}
                />
              </div>
            );
          })}
        </div>
      </div>
    );

  return (
    <TabsContent value="connectors" className="space-y-3">
      <div className="settings-subpanel-compact">
        <div className="mb-3 text-sm font-medium">Built-in Tools</div>
        <div
          className={[
            'flex items-center justify-between gap-3 rounded-xl border px-4 py-3 shadow-sm transition-opacity',
            sandboxEnabled ? 'border-border/50 bg-card' : 'border-border/30 bg-muted/40 opacity-60',
          ].join(' ')}
        >
          <div>
            <div className="text-sm font-medium">
              SuperExec{' '}
              <span className="text-xs font-normal text-muted-foreground">
                (run terminal commands inside the sandbox)
              </span>
            </div>
            <div className="mt-0.5 text-xs text-muted-foreground">
              {!sandboxEnabled
                ? 'Enable sandbox to use SuperExec.'
                : agent.superExecMode === 'trusted'
                  ? 'The agent can run shell commands without approval.'
                  : agent.superExecMode === 'ask'
                    ? 'Each shell command requires manual approval before execution.'
                    : 'Shell command execution is disabled for this agent.'}
            </div>
          </div>
          <SegmentedControl
            className="shrink-0"
            value={agent.superExecMode}
            disabled={!sandboxEnabled}
            onChange={(option) => onPatch((previous) => ({ ...previous, superExecMode: option }))}
            options={[
              { value: 'disabled', label: 'Disabled', tone: 'neutral' },
              { value: 'trusted', label: 'Trusted', tone: 'primary' },
              { value: 'ask', label: 'Ask first', tone: 'attention' },
            ]}
          />
        </div>
      </div>

      {connectors.length > 0 ? (
        <div className="space-y-3">
          {renderSection(
            'Remote MCPs',
            'Remote connectors run over HTTP/SSE and use the standard connector permission controls.',
            groups.remote
          )}
          {renderSection(
            'MCP Bundles',
            'Imported MCP bundles can run in different execution contexts. The default is isolated execution.',
            groups.bundles,
            (connector) => {
              const connectorId = String(connector.id);
              const selectedContext = getBundleExecutionContext(connectorId);
              return (
                <div className="space-y-2">
                  <div className="text-sm font-medium">Execution Context</div>
                  <SegmentedControl
                    value={selectedContext}
                    onChange={(next) => upsertBinding(connectorId, next)}
                    options={bundleExecutionContextOptions.map((option) => ({
                      value: option.value,
                      label: option.label,
                      disabled:
                        (option.value === agentSandboxExecutionContext && !sandboxEnabled) ||
                        getConnectorMode(connectorId) === 'off' ||
                        !selectionEnabled,
                    }))}
                  />
                </div>
              );
            }
          )}
          {renderSection(
            'Container MCPs',
            'Containerized connectors use the standard connector permission controls.',
            groups.containers
          )}
        </div>
      ) : (
        <p className="text-sm text-muted-foreground">
          No connectors configured yet. Add MCP connectors from the Connectors page.
        </p>
      )}

      <div className="settings-subpanel-compact">
        <div className="mb-1 text-sm font-medium">Agent Tools</div>
        <p className="mb-3 text-xs text-muted-foreground">
          Select agents this agent can invoke as tools. Agents must be configured to expose
          themselves first.
        </p>
        {agentToolSelectionLocked ? (
          <Alert variant="attention" className="mb-3 px-3 py-2 text-xs">
            This agent is exposed as a tool, so it cannot invoke other agents as tools. Clear any
            existing agent tool selections or disable Expose as tool before saving.
          </Alert>
        ) : null}
        <div className="space-y-2">
          {otherAgents
            .filter((candidate) => candidate.toolExposure.exposedAsTool)
            .map((candidate) => {
              const mode = getAgentToolMode(candidate.id);
              return (
                <div
                  key={candidate.id}
                  className="flex items-center gap-3 rounded-xl border border-border/50 bg-card px-4 py-3 shadow-sm"
                >
                  <span className="min-w-0 flex-1 truncate text-sm font-medium">
                    {candidate.name || candidate.id}
                  </span>
                  <SegmentedControl
                    value={mode}
                    onChange={(option) => onAgentToolModeChange(candidate.id, option)}
                    options={[
                      { value: 'off', label: 'Disabled' },
                      { value: 'on', label: 'Trusted', disabled: agentToolSelectionLocked },
                      { value: 'ask', label: 'Ask first', disabled: agentToolSelectionLocked },
                    ]}
                  />
                </div>
              );
            })}
          {otherAgents.filter((candidate) => candidate.toolExposure.exposedAsTool).length === 0 ? (
            <p className="text-xs text-muted-foreground">
              No agents are currently exposed as tools.
            </p>
          ) : null}
        </div>
      </div>
    </TabsContent>
  );
}

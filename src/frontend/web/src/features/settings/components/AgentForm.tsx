import { Alert } from '@/components/ui/alert';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { SegmentedControl } from '@/components/ui/segmented-control';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { Textarea } from '@/components/ui/textarea';
import type { Connector } from '@/features/connectors';
import type { Skill } from '@/features/skills';
import type { AgentToolMode } from '../hooks/useAgentsTab';
import { evaluateToolExposure } from '../policy/toolPolicy';
import type {
  AgentToolPolicyProjection,
  ToolPolicyCategoryCode,
  ToolPolicyDecisionProjection,
  AgentSettingsEntry,
  ModelEntry,
  OrganizationSettingsEntry,
  PersonaDefinitions,
  SystemRuntimeLimitsSettings,
} from '../types/settingsTypes';
import { CapabilityBadge } from './SettingsShared';
import { AgentPersonaEditor } from './AgentPersonaEditor';
import { AgentConnectorsEditor } from './AgentConnectorsEditor';
import { AgentSkillsEditor } from './AgentSkillsEditor';
import { AgentRuntimeEditor } from './AgentRuntimeEditor';
import { AgentChannelsTab } from './AgentChannelsTab';
import { normalizeSystemRuntimeLimits } from '../utils/settingsUtils';

const agentSandboxExecutionContext = 'AgentContext';
const defaultBundleExecutionContext = 'OwnContext';

interface AgentFormProps {
  variant?: 'wizard' | 'settings';
  agent: AgentSettingsEntry;
  agents: AgentSettingsEntry[];
  models: ModelEntry[];
  personaDefinitions: PersonaDefinitions;
  organizationDefaults?: OrganizationSettingsEntry | null;
  platformRuntime?: SystemRuntimeLimitsSettings;
  toolPolicyProjection?: AgentToolPolicyProjection | null;
  mcpServers: Connector[];
  skills: Skill[];
  isToolPanelOpen: (connectorId: string) => boolean;
  getConnectorMode: (connectorId: string) => 'off' | 'on' | 'advanced';
  getToolMode: (connectorId: string, toolName: string) => 'off' | 'on' | 'ask';
  getAgentToolMode: (targetAgentId: string) => AgentToolMode;
  onPatch: (updater: (prev: AgentSettingsEntry) => AgentSettingsEntry) => void;
  onModeChange: (connectorId: string, mode: 'off' | 'on' | 'advanced') => void;
  onTogglePanel: (connectorId: string) => void;
  onToolModeChange: (connectorId: string, toolName: string, mode: 'off' | 'on' | 'ask') => void;
  onAgentToolModeChange: (targetAgentId: string, mode: AgentToolMode) => void;
}

export function AgentForm({
  variant = 'settings',
  agent,
  agents,
  models,
  personaDefinitions,
  organizationDefaults,
  platformRuntime = normalizeSystemRuntimeLimits(),
  toolPolicyProjection = null,
  mcpServers,
  skills,
  isToolPanelOpen,
  getConnectorMode,
  getToolMode,
  getAgentToolMode,
  onPatch,
  onModeChange,
  onTogglePanel,
  onToolModeChange,
  onAgentToolModeChange,
}: Readonly<AgentFormProps>) {
  const selectedModel = models.find((m) => m.id === agent.modelId);
  const modelOptions = [...models].sort(
    (a, b) => a.id.localeCompare(b.id) || a.modelName.localeCompare(b.modelName)
  );
  const selectedCapabilities = selectedModel?.capabilities;
  const hasSelectedCapabilities = Boolean(selectedCapabilities);
  const capabilities = selectedCapabilities ?? null;
  const connectorSelectionDecision = resolveExposureDecision(
    'connector',
    agent,
    toolPolicyProjection
  );
  const connectorSelectionEnabled = connectorSelectionDecision.allowed;
  const skillSelectionDecision = resolveExposureDecision('skill', agent, toolPolicyProjection);
  const skillSelectionEnabled = skillSelectionDecision.allowed;

  const personaTones = personaDefinitions.tones;
  const personaVerbosity = personaDefinitions.verbosity_levels;

  const getModelOptionLabel = (model: ModelEntry): string =>
    model.id.trim() || model.modelName.trim();

  let selectedModelLabel = '';
  if (selectedModel) {
    selectedModelLabel = getModelOptionLabel(selectedModel);
  }

  return (
    <div className="space-y-3">
      {variant === 'wizard' && (
        <>
          <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
            <div>
              <label
                className="mb-1 block text-xs text-muted-foreground"
                htmlFor={`agent-name-${agent.id}`}
              >
                Name
              </label>
              <Input
                id={`agent-name-${agent.id}`}
                value={agent.name}
                onChange={(e) => onPatch((prev) => ({ ...prev, name: e.target.value }))}
              />
            </div>
            <div>
              <label
                className="mb-1 block text-xs text-muted-foreground"
                htmlFor={`agent-model-${agent.id}`}
              >
                Model
              </label>
              <div className="flex min-h-10 w-full items-center rounded-xl border border-input bg-background/90 px-3 py-2 text-sm shadow-sm shadow-black/2">
                {selectedModelLabel || 'Choose a model in the previous step'}
              </div>
              <p className="mt-1 text-xs text-muted-foreground">
                Using the model you picked in the previous step.
              </p>
              {!hasSelectedCapabilities && agent.modelId && (
                <p className="mt-1 text-xs text-muted-foreground">
                  No capability data for this model. Run Check in Models.
                </p>
              )}
              {capabilities?.agentConversations === false && (
                <p className="mt-1 text-xs text-attention">
                  This model may not work for agent conversations
                </p>
              )}
              {capabilities && (
                <div className="mt-2 rounded border border-border p-2 text-xs">
                  <div className="mb-1 font-medium">Capability advisory</div>
                  <div className="flex flex-wrap gap-1">
                    <CapabilityBadge label="Agent Chat" value={capabilities.agentConversations} />
                    <CapabilityBadge label="Tools" value={capabilities.toolCalling} />
                    <CapabilityBadge label="Streaming" value={capabilities.streaming} />
                    <CapabilityBadge label="Structured" value={capabilities.structuredJson} />
                    <CapabilityBadge label="Reasoning" value={capabilities.reasoningOnlyOutput} />
                  </div>
                </div>
              )}
            </div>
          </div>

          <div>
            <label
              className="mb-1 block text-xs text-muted-foreground"
              htmlFor={`agent-system-prompt-${agent.id}`}
            >
              {variant === 'wizard' ? 'Persona Instructions (Who are you?)' : 'System Prompt'}
            </label>
            <Textarea
              id={`agent-system-prompt-${agent.id}`}
              value={agent.systemPrompt ?? ''}
              onChange={(e) => onPatch((prev) => ({ ...prev, systemPrompt: e.target.value }))}
              rows={4}
              placeholder="e.g. You are a helpful assistant..."
            />
          </div>

          <div className="space-y-3">
            <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
              <div>
                <label
                  className="mb-1 block text-xs text-muted-foreground"
                  htmlFor={`agent-persona-tone-${agent.id}`}
                >
                  Tone
                </label>
                <select
                  id={`agent-persona-tone-${agent.id}`}
                  title="Agent persona tone"
                  className="input w-full"
                  value={agent.persona.tone}
                  onChange={(e) =>
                    onPatch((prev) => ({
                      ...prev,
                      persona: {
                        ...prev.persona,
                        tone: e.target.value,
                      },
                    }))
                  }
                >
                  {personaTones.map((tone) => (
                    <option key={tone} value={tone}>
                      {tone}
                    </option>
                  ))}
                </select>
              </div>

              <div>
                <label
                  className="mb-1 block text-xs text-muted-foreground"
                  htmlFor={`agent-persona-verbosity-${agent.id}`}
                >
                  Verbosity
                </label>
                <select
                  id={`agent-persona-verbosity-${agent.id}`}
                  title="Agent persona verbosity"
                  className="input w-full"
                  value={agent.persona.verbosity}
                  onChange={(e) =>
                    onPatch((prev) => ({
                      ...prev,
                      persona: {
                        ...prev.persona,
                        verbosity: e.target.value,
                      },
                    }))
                  }
                >
                  {personaVerbosity.map((level) => (
                    <option key={level} value={level}>
                      {level}
                    </option>
                  ))}
                </select>
              </div>
            </div>

            <p className="text-xs text-muted-foreground">
              You can customize your agent's personality more deeply, including tone, style,
              response variety, and company or office information in the Settings area.
            </p>

            <Alert variant="success" className="p-3">
              <div className="text-sm font-medium">
                Sandbox & Tools
              </div>
              <div className="mt-0.5 text-xs">
                Your agent starts with a secure sandbox available for tool-based work, but no extra
                tools are turned on here in the wizard. Later, in Settings, you can choose which
                tools and connectors this agent is allowed to use.
              </div>
            </Alert>
            <div className="rounded-lg border border-border bg-muted/30 p-3">
              <div className="text-sm font-medium text-foreground">Approvals & Review</div>
              <div className="mt-0.5 text-xs text-muted-foreground">
                If you want this agent to ask before doing certain sensitive actions, you can set
                that up later in Settings. When approval is required, the request will appear in the
                Approvals area for someone to review.
              </div>
            </div>
            <Alert variant="info" className="p-3">
              <div className="text-sm font-medium">Connectors</div>
              <div className="mt-0.5 text-xs">
                Connectors let your agent use external tools and integrations, like services, APIs,
                or workspace actions. Later, open the Connectors page to add or enable connectors,
                then go to Settings for this agent to choose which connectors and tools it can use.
              </div>
            </Alert>
            <Alert variant="info" className="p-3">
              <div className="text-sm font-medium">Skills</div>
              <div className="mt-0.5 text-xs">
                Skills are reusable packaged capabilities that teach an agent how to handle a kind
                of task. Later, open the Skills page to import the skills you want, then return to
                this agent in Settings and turn on the skills it should use.
              </div>
            </Alert>
          </div>
        </>
      )}

      {variant === 'settings' && (
        <>
          <Tabs defaultValue="main" className="mt-1">
            <TabsList className="mb-3">
              <TabsTrigger value="main">Main</TabsTrigger>
              <TabsTrigger value="persona">Persona</TabsTrigger>
              <TabsTrigger value="connectors">Connectors</TabsTrigger>
              <TabsTrigger value="skills">Skills</TabsTrigger>
              <TabsTrigger value="runtime">Runtime</TabsTrigger>
              <TabsTrigger value="channels">Channels</TabsTrigger>
            </TabsList>

            {/* â”€â”€ Main â”€â”€ */}
            <TabsContent value="main" className="space-y-3">
              <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
                <div>
                  <label
                    className="mb-1 block text-xs text-muted-foreground"
                    htmlFor={`agent-name-main-${agent.id}`}
                  >
                    Name
                  </label>
                  <Input
                    id={`agent-name-main-${agent.id}`}
                    value={agent.name}
                    onChange={(e) => onPatch((prev) => ({ ...prev, name: e.target.value }))}
                  />
                </div>
                <div>
                  <label
                    className="mb-1 block text-xs text-muted-foreground"
                    htmlFor={`agent-model-main-${agent.id}`}
                  >
                    Model
                  </label>
                  <select
                    id={`agent-model-main-${agent.id}`}
                    title="Agent model"
                    className="input w-full"
                    value={agent.modelId}
                    onChange={(e) => onPatch((prev) => ({ ...prev, modelId: e.target.value }))}
                  >
                    <option value="">Select model...</option>
                    {modelOptions.map((m) => (
                      <option key={m.id} value={m.id}>
                        {getModelOptionLabel(m)}
                      </option>
                    ))}
                  </select>
                  {!hasSelectedCapabilities && agent.modelId && (
                    <p className="mt-1 text-xs text-muted-foreground">
                      No capability data for this model. Run Check in Models.
                    </p>
                  )}
                  {capabilities?.agentConversations === false && (
                    <p className="mt-1 text-xs text-attention">
                      This model may not work for agent conversations
                    </p>
                  )}
                  {capabilities && (
                    <div className="mt-2 rounded border border-border p-2 text-xs">
                      <div className="mb-1 font-medium">Capability advisory</div>
                      <div className="flex flex-wrap gap-1">
                        <CapabilityBadge
                          label="Agent Chat"
                          value={capabilities.agentConversations}
                        />
                        <CapabilityBadge label="Tools" value={capabilities.toolCalling} />
                        <CapabilityBadge label="Streaming" value={capabilities.streaming} />
                        <CapabilityBadge label="Structured" value={capabilities.structuredJson} />
                        <CapabilityBadge
                          label="Reasoning"
                          value={capabilities.reasoningOnlyOutput}
                        />
                      </div>
                    </div>
                  )}
                </div>
              </div>

              <div>
                <label
                  className="mb-1 block text-xs text-muted-foreground"
                  htmlFor={`agent-system-prompt-main-${agent.id}`}
                >
                  System Prompt
                </label>
                <Textarea
                  id={`agent-system-prompt-main-${agent.id}`}
                  value={agent.systemPrompt ?? ''}
                  onChange={(e) => onPatch((prev) => ({ ...prev, systemPrompt: e.target.value }))}
                  rows={6}
                />
              </div>

              <div className="flex items-center justify-between gap-3 rounded-xl border border-border/50 bg-card px-4 py-3 shadow-sm">
                <div>
                  <div className="text-sm font-medium">Sandbox</div>
                  <div className="mt-0.5 text-xs text-muted-foreground">
                    {agent.sandbox.enabled
                      ? 'Sandbox-backed capabilities are available to this agent.'
                      : 'Sandbox-backed capabilities are disabled for this agent.'}
                  </div>
                </div>
                <SegmentedControl
                  value={agent.sandbox.enabled ? 'on' : 'off'}
                  onChange={(value) =>
                    onPatch((prev) => ({
                      ...prev,
                      sandbox: { ...prev.sandbox, enabled: value === 'on' },
                      mcpBindings:
                        value === 'on'
                          ? prev.mcpBindings
                          : prev.mcpBindings.map((binding) =>
                              binding.context === agentSandboxExecutionContext
                                ? { ...binding, context: defaultBundleExecutionContext }
                                : binding
                            ),
                    }))
                  }
                  options={[
                    { value: 'off', label: 'Disabled', tone: 'neutral' },
                    { value: 'on', label: 'Enabled', tone: 'neutral' },
                  ]}
                  className="shrink-0"
                />
              </div>

              <div className="settings-subpanel">
                <div className="mb-3 text-sm font-medium">Agent as Tool</div>
                <div
                  className={`settings-option-card ${agent.toolExposure.exposedAsTool ? 'settings-option-card-active' : 'settings-option-card-inactive'}`}
                >
                  <div className="flex items-start gap-3">
                    <div className="mt-0.5 shrink-0">
                      <Checkbox
                        checked={agent.toolExposure.exposedAsTool}
                        onChange={(e) =>
                          onPatch((prev) => ({
                            ...prev,
                            toolExposure: { ...prev.toolExposure, exposedAsTool: e.target.checked },
                          }))
                        }
                      />
                    </div>
                    <div>
                      <div
                        className={[
                          'text-sm font-medium',
                          agent.toolExposure.exposedAsTool
                            ? 'text-primary'
                            : 'text-foreground',
                        ].join(' ')}
                      >
                        Expose this agent as a tool
                        <span className="ml-1 text-[10px] font-normal uppercase tracking-wide text-muted-foreground">
                          Experimental
                        </span>
                      </div>
                      <div
                        className={[
                          'mt-0.5 text-xs',
                          agent.toolExposure.exposedAsTool
                            ? 'text-primary'
                            : 'text-muted-foreground',
                        ].join(' ')}
                      >
                        {agent.toolExposure.exposedAsTool
                          ? 'Permitted agents can invoke this agent as a tool.'
                          : 'This agent is not available as a tool for other agents.'}
                      </div>
                    </div>
                  </div>
                </div>

                {agent.toolExposure.exposedAsTool && (
                  <div className="mt-3 space-y-3">
                    <div>
                      <label
                        className="mb-1 block text-xs text-muted-foreground"
                        htmlFor={`agent-tool-desc-main-${agent.id}`}
                      >
                        Tool description
                      </label>
                      <Textarea
                        id={`agent-tool-desc-main-${agent.id}`}
                        value={agent.toolExposure.description}
                        onChange={(e) =>
                          onPatch((prev) => ({
                            ...prev,
                            toolExposure: { ...prev.toolExposure, description: e.target.value },
                          }))
                        }
                        rows={2}
                        placeholder="Describe what this agent does, so callers know when to invoke it."
                      />
                    </div>
                  </div>
                )}
              </div>

              <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
                <div>
                  <label
                    className="mb-1 block text-xs text-muted-foreground"
                    htmlFor={`agent-max-history-runs-${agent.id}`}
                  >
                    Retained sessions
                  </label>
                  <Input
                    id={`agent-max-history-runs-${agent.id}`}
                    type="number"
                    min={1}
                    value={agent.session.maxHistoryRuns}
                    onChange={(e) =>
                      onPatch((prev) => ({
                        ...prev,
                        session: {
                          ...prev.session,
                          historyEnabled: true,
                          maxHistoryRuns: Math.max(1, Number(e.target.value) || 1),
                        },
                      }))
                    }
                  />
                  <p className="mt-1 text-xs text-muted-foreground">
                    Current-session context is always preserved. This controls how many older
                    sessions are retained after a new session starts.
                  </p>
                </div>
                <div>
                  <label
                    className="mb-1 block text-xs text-muted-foreground"
                    htmlFor={`agent-summary-model-${agent.id}`}
                  >
                    Summary model
                  </label>
                  <select
                    id={`agent-summary-model-${agent.id}`}
                    title="Agent summary model"
                    className="input w-full"
                    value={agent.session.summaryModelId ?? ''}
                    onChange={(e) =>
                      onPatch((prev) => ({
                        ...prev,
                        session: { ...prev.session, summaryModelId: e.target.value || null },
                      }))
                    }
                  >
                    <option value="">Use agent model</option>
                    {modelOptions.map((m) => (
                      <option key={m.id} value={m.id}>
                        {getModelOptionLabel(m)}
                      </option>
                    ))}
                  </select>
                </div>
              </div>

              <div className="settings-subpanel">
                <div
                  className={`settings-option-card ${agent.session.memoryFactExtractionEnabled ? 'settings-option-card-active' : 'settings-option-card-inactive'}`}
                >
                  <div className="flex items-start gap-3">
                    <div className="mt-0.5 shrink-0">
                      <Checkbox
                        checked={agent.session.memoryFactExtractionEnabled}
                        onChange={(e) =>
                          onPatch((prev) => ({
                            ...prev,
                            session: {
                              ...prev.session,
                              memoryFactExtractionEnabled: e.target.checked,
                            },
                          }))
                        }
                      />
                    </div>
                    <div>
                      <div
                        className={[
                          'text-sm font-medium',
                          agent.session.memoryFactExtractionEnabled
                            ? 'text-primary'
                            : 'text-foreground',
                        ].join(' ')}
                      >
                        Extract and persist memory facts after each run
                      </div>
                      <div
                        className={[
                          'mt-0.5 text-xs',
                          agent.session.memoryFactExtractionEnabled
                            ? 'text-primary'
                            : 'text-muted-foreground',
                        ].join(' ')}
                      >
                        {agent.session.memoryFactExtractionEnabled
                          ? 'Completed turns can extract and persist memory facts for this agent.'
                          : 'No post-run memory facts will be extracted or persisted for this agent.'}
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            </TabsContent>

            <AgentPersonaEditor agent={agent} definitions={personaDefinitions} onPatch={onPatch} />

            {/* â”€â”€ Connectors â”€â”€ */}
            <AgentConnectorsEditor
              agent={agent}
              agents={agents}
              connectors={mcpServers}
              selectionEnabled={connectorSelectionEnabled}
              selectionMessage={connectorSelectionDecision.message}
              isToolPanelOpen={isToolPanelOpen}
              getConnectorMode={getConnectorMode}
              getToolMode={getToolMode}
              getAgentToolMode={getAgentToolMode}
              onPatch={onPatch}
              onModeChange={onModeChange}
              onTogglePanel={onTogglePanel}
              onToolModeChange={onToolModeChange}
              onAgentToolModeChange={onAgentToolModeChange}
            />

            {/* â”€â”€ Skills â”€â”€ */}
            <AgentSkillsEditor
              agent={agent}
              skills={skills}
              selectionEnabled={skillSelectionEnabled}
              onPatch={onPatch}
            />

            <AgentRuntimeEditor
              agent={agent}
              platformRuntime={platformRuntime}
              onPatch={onPatch}
            />

            {agent.id ? <AgentChannelsTab agentId={agent.id} /> : null}
          </Tabs>
        </>
      )}
    </div>
  );
}

function getProjectedExposureDecision(
  projection: AgentToolPolicyProjection | null,
  category: ToolPolicyCategoryCode
): ToolPolicyDecisionProjection | null {
  if (!projection) {
    return null;
  }

  const categoryProjection = projection.categories.find((entry) => entry.category === category);
  return categoryProjection?.exposure ?? null;
}

function resolveExposureDecision(
  category: ToolPolicyCategoryCode,
  agent: Pick<AgentSettingsEntry, 'sandbox' | 'superExecMode'>,
  projection: AgentToolPolicyProjection | null
): ToolPolicyDecisionProjection {
  const localDecision = evaluateToolExposure(category, agent);
  const projectedDecision = getProjectedExposureDecision(projection, category);

  if (!projectedDecision) {
    return localDecision;
  }

  // The projection describes the last saved agent. While editing, sandbox and
  // SuperExec changes need to take effect immediately in the form.
  if (!localDecision.allowed && projectedDecision.allowed) {
    return localDecision;
  }

  return projectedDecision;
}

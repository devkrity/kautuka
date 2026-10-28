import { fetchConnectors, type Connector } from '@/features/connectors';
import { settingsAdmin } from '@/features/settings';
import { fetchSkills, normalizeAgentSkillAttachments, type Skill } from '@/features/skills';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';
import { toast } from 'sonner';
import { settingsQueryKeys } from '../api/queryKeys';
import type { ConnectorMode, ToolMode } from '../components/shared/McpPanel';
import type {
  AgentMcpBindingEntry,
  AgentSettingsEntry,
  ModelEntry,
  OrganizationSettingsEntry,
  PersonaDefinitions,
  SystemRuntimeLimitsSettings,
} from '../types/settingsTypes';
import {
  apiError,
  normalizeAgentRuntime,
  normalizeApprovalConfig,
  normalizePersonaDefinitions,
  normalizePersonaProfile,
  normalizeSystemRuntimeLimits,
  normalizeToolExposure,
} from '../utils/settingsUtils';

export type AgentToolMode = 'off' | 'on' | 'ask';

const defaultBindingContext = 'OwnContext';

const exposedAgentToolLeafValidationMessage =
  'Agents exposed as tools cannot invoke other agents as tools. Remove agent tool selections or disable Expose as tool.';

const defaultPlatformRuntime: SystemRuntimeLimitsSettings = normalizeSystemRuntimeLimits();

const getMapValues = (source: Record<string, string[]> | undefined, key: string): string[] => {
  const values = new Map(Object.entries(source ?? {})).get(key);
  return Array.isArray(values) ? values : [];
};

const buildFilteredRecord = (
  source: Record<string, string[]> | undefined,
  keyToRemove: string
): Record<string, string[]> => {
  const next = new Map<string, string[]>();
  for (const [key, value] of Object.entries(source ?? {})) {
    if (key === keyToRemove || !Array.isArray(value) || value.length === 0) {
      continue;
    }
    next.set(key, value);
  }
  return Object.fromEntries(next);
};

const withKeyValues = (
  source: Record<string, string[]> | undefined,
  key: string,
  values: string[]
): Record<string, string[]> => {
  const next = new Map(Object.entries(source ?? {}));
  if (values.length > 0) {
    next.set(key, values);
  } else {
    next.delete(key);
  }
  return Object.fromEntries(next);
};

const withRecordValue = <TValue>(
  source: Record<string, TValue>,
  key: string,
  value: TValue
): Record<string, TValue> => {
  const next = new Map(Object.entries(source));
  next.set(key, value);
  return Object.fromEntries(next) as Record<string, TValue>;
};

const normalizeAgentBindings = (
  bindings: AgentMcpBindingEntry[] | undefined
): AgentMcpBindingEntry[] => {
  const bindingEntries = bindings ?? [];
  return bindingEntries
    .filter((binding): binding is AgentMcpBindingEntry => Boolean(binding))
    .filter((binding) => binding.connectorId.trim().length > 0)
    .map((binding) => ({
      connectorId: binding.connectorId.trim(),
      context: binding.context?.trim() || defaultBindingContext,
    }));
};

const getAgentConnectorIds = (agent: AgentSettingsEntry): string[] => {
  return (agent.mcpBindings ?? [])
    .map((binding) => binding.connectorId)
    .filter((connectorId, index, allIds) => allIds.indexOf(connectorId) === index);
};

const setAgentConnectorIds = (
  agent: AgentSettingsEntry,
  connectorIds: string[]
): AgentSettingsEntry => {
  const normalizedIds = connectorIds.filter(
    (connectorId, index, allIds) => allIds.indexOf(connectorId) === index
  );
  const existingBindings = normalizeAgentBindings(agent.mcpBindings);

  return {
    ...agent,
    mcpBindings: normalizedIds.map((connectorId) => {
      const existingBinding = existingBindings.find(
        (binding) => binding.connectorId === connectorId
      );
      return existingBinding ?? { connectorId, context: defaultBindingContext };
    }),
  };
};

export function useAgentsTab() {
  const queryClient = useQueryClient();
  const [agents, setAgents] = useState<AgentSettingsEntry[]>([]);
  const [models, setModels] = useState<ModelEntry[]>([]);
  const [mcpServers, setMcpServers] = useState<Connector[]>([]);
  const [skills, setSkills] = useState<Skill[]>([]);
  const [loading, setLoading] = useState(true);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [savingId, setSavingId] = useState<string | null>(null);
  const [openToolPanels, setOpenToolPanels] = useState<Set<string>>(() => new Set());
  const [draftAgent, setDraftAgent] = useState<AgentSettingsEntry | null>(null);
  const [personaDefinitions, setPersonaDefinitions] = useState<PersonaDefinitions>(
    normalizePersonaDefinitions(undefined)
  );
  const [organizationDefaults, setOrganizationDefaults] =
    useState<OrganizationSettingsEntry | null>(null);
  const [platformRuntime, setPlatformRuntime] =
    useState<SystemRuntimeLimitsSettings>(defaultPlatformRuntime);

  const load = async () => {
    setLoading(true);
    try {
      const [ar, mr, connectors, personaDefinitionsResponse, platformSkills, orgResponse, runtimeLimitsResponse] =
        await Promise.all([
          settingsAdmin.listAgents(),
          settingsAdmin.listModels(),
          fetchConnectors(),
          settingsAdmin.getPersonaDefinitions(),
          fetchSkills(),
          settingsAdmin.getOrganization(),
          settingsAdmin.getRuntimeLimits(),
        ]);
      const definitions = normalizePersonaDefinitions(personaDefinitionsResponse.data.definitions);
      const nextAgents: AgentSettingsEntry[] = ar.data.agents.map((agent) => ({
        ...agent,
        scope: agent.scope === 'system' ? 'system' : 'user',
        enabled: Boolean(agent.enabled ?? true),
        ownerUserId: agent.ownerUserId ?? '',
        mcpBindings: normalizeAgentBindings((agent as AgentSettingsEntry).mcpBindings),
        session: {
          historyEnabled: Boolean(agent.session?.historyEnabled ?? true),
          maxHistoryRuns: Math.max(1, Number(agent.session?.maxHistoryRuns ?? 5)),
          summariesEnabled: Boolean(agent.session?.summariesEnabled ?? true),
          summaryModelId: agent.session?.summaryModelId ?? null,
          memoryFactExtractionEnabled: Boolean(agent.session?.memoryFactExtractionEnabled ?? true),
        },
        skillAttachments: normalizeAgentSkillAttachments(agent.skillAttachments),
        approvalConfig: normalizeApprovalConfig(agent.approvalConfig),
        persona: normalizePersonaProfile(agent.persona, definitions),
        toolExposure: normalizeToolExposure(
          agent.toolExposure as unknown as Record<string, unknown>
        ),
        runtime: normalizeAgentRuntime(agent.runtime as unknown as Record<string, unknown>),
      }));

      setPersonaDefinitions(definitions);
      setOrganizationDefaults(orgResponse.data.organization);
      setPlatformRuntime(normalizeSystemRuntimeLimits(runtimeLimitsResponse.data));
      setAgents(nextAgents);
      queryClient.setQueryData(
        settingsQueryKeys.agents(),
        nextAgents.map((agent) => ({
          id: agent.id,
          name: agent.name,
          scope: agent.scope,
          ownerUserId: agent.ownerUserId || null,
          enabled: agent.enabled,
          modelId: agent.modelId,
          sandbox: agent.sandbox,
          persona: {
            icon: agent.persona.icon,
            color: agent.persona.color,
          },
        }))
      );
      setModels(mr.data.models);
      setMcpServers(connectors.filter((c) => c.enabled));
      setSkills(platformSkills);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void load();
  }, []);

  const patchAgent = (id: string, updater: (prev: AgentSettingsEntry) => AgentSettingsEntry) => {
    if (id === '') {
      setDraftAgent((prev) => (prev ? updater(prev) : prev));
    } else {
      setAgents((prev) => prev.map((agent) => (agent.id === id ? updater(agent) : agent)));
    }
  };

  const getConnectorMode = (agent: AgentSettingsEntry, connectorId: string): ConnectorMode => {
    const approvalCfg = normalizeApprovalConfig(agent.approvalConfig);
    const isIncluded = getAgentConnectorIds(agent).includes(connectorId);
    const isAdvanced = isIncluded && approvalCfg.requireMcpIds.includes(connectorId);
    if (isIncluded === false) {
      return 'off';
    }
    if (isAdvanced) {
      return 'advanced';
    }
    return 'on';
  };

  const setConnectorMode = (agentId: string, connectorId: string, mode: ConnectorMode) => {
    const panelKey = `${agentId}:${connectorId}`;
    patchAgent(agentId, (prev) => {
      const cfg = normalizeApprovalConfig(prev.approvalConfig);
      let ids = getAgentConnectorIds(prev);
      let reqIds = cfg.requireMcpIds;
      let reqFilters = cfg.requireMcpFunctionFilters;
      let disabledFns = cfg.disabledMcpFunctions;

      if (mode === 'off') {
        ids = ids.filter((x) => x !== connectorId);
        reqIds = reqIds.filter((x) => x !== connectorId);
        reqFilters = buildFilteredRecord(reqFilters, connectorId);
        disabledFns = buildFilteredRecord(disabledFns, connectorId);
      } else if (mode === 'on') {
        if (!ids.includes(connectorId)) ids = [...ids, connectorId];
        reqIds = reqIds.filter((x) => x !== connectorId);
        reqFilters = buildFilteredRecord(reqFilters, connectorId);
        disabledFns = buildFilteredRecord(disabledFns, connectorId);
      } else {
        if (!ids.includes(connectorId)) ids = [...ids, connectorId];
        if (!reqIds.includes(connectorId)) reqIds = [...reqIds, connectorId];
      }

      return {
        ...setAgentConnectorIds(prev, ids),
        approvalConfig: {
          ...cfg,
          requireMcpIds: reqIds,
          requireMcpFunctionFilters: reqFilters,
          disabledMcpFunctions: disabledFns,
        },
      };
    });

    if (mode !== 'advanced') {
      setOpenToolPanels((prev) => {
        const next = new Set(prev);
        next.delete(panelKey);
        return next;
      });
    }
  };

  const getToolMode = (
    agent: AgentSettingsEntry,
    connectorId: string,
    toolName: string
  ): ToolMode => {
    const cfg = normalizeApprovalConfig(agent.approvalConfig);
    const disabled = getMapValues(cfg.disabledMcpFunctions, connectorId);
    const filters = getMapValues(cfg.requireMcpFunctionFilters, connectorId);
    if (disabled.includes(toolName)) return 'off';
    if (filters.includes(toolName)) return 'ask';
    return 'on';
  };

  const setToolMode = (
    agentId: string,
    connectorId: string,
    toolName: string,
    toolMode: ToolMode
  ) => {
    patchAgent(agentId, (prev) => {
      const cfg = normalizeApprovalConfig(prev.approvalConfig);
      let disabled = withKeyValues(
        cfg.disabledMcpFunctions,
        connectorId,
        getMapValues(cfg.disabledMcpFunctions, connectorId).filter((t) => t !== toolName)
      );
      let filters = withKeyValues(
        cfg.requireMcpFunctionFilters,
        connectorId,
        getMapValues(cfg.requireMcpFunctionFilters, connectorId).filter((t) => t !== toolName)
      );
      if (toolMode === 'off') {
        disabled = withKeyValues(disabled, connectorId, [
          ...getMapValues(disabled, connectorId),
          toolName,
        ]);
      } else if (toolMode === 'ask') {
        filters = withKeyValues(filters, connectorId, [
          ...getMapValues(filters, connectorId),
          toolName,
        ]);
      }
      return {
        ...prev,
        approvalConfig: {
          ...cfg,
          disabledMcpFunctions: disabled,
          requireMcpFunctionFilters: filters,
        },
      };
    });
  };

  const toggleToolPanel = (agentId: string, connectorId: string) => {
    const panelKey = `${agentId}:${connectorId}`;
    setOpenToolPanels((prev) => {
      const next = new Set(prev);
      if (next.has(panelKey)) next.delete(panelKey);
      else next.add(panelKey);
      return next;
    });
  };

  const isToolPanelOpen = (agentId: string, connectorId: string) =>
    openToolPanels.has(`${agentId}:${connectorId}`);

  const saveAgent = async (agent: AgentSettingsEntry) => {
    const isCreate = !agent.id.trim();
    const resolvedId = isCreate ? `agent-${Date.now()}` : agent.id;
    const approvalConfig = normalizeApprovalConfig(agent.approvalConfig);

    if (!agent.name.trim()) {
      toast.error('Agent name is required.');
      return null;
    }
    if (!agent.modelId.trim()) {
      toast.error('Agent model is required.');
      return null;
    }
    if (agent.toolExposure.exposedAsTool && approvalConfig.agentToolIds.length > 0) {
      toast.error(exposedAgentToolLeafValidationMessage, { duration: Infinity });
      return null;
    }

    setSavingId(resolvedId);
    try {
      await settingsAdmin.saveAgent(resolvedId, {
        name: agent.name.trim(),
        modelId: agent.modelId,
        systemPrompt: agent.systemPrompt,
        superExecMode: agent.superExecMode,
        mcpBindings: agent.mcpBindings ?? [],
        skillAttachments: agent.skillAttachments ?? [],
        approvalConfig,
        persona: normalizePersonaProfile(agent.persona, personaDefinitions),
        toolExposure: agent.toolExposure,
        runtime: normalizeAgentRuntime(agent.runtime as unknown as Record<string, unknown>),
        session: {
          ...agent.session,
          historyEnabled: true,
          maxHistoryRuns: Math.max(1, agent.session.maxHistoryRuns),
          memoryFactExtractionEnabled: agent.session.memoryFactExtractionEnabled ?? true,
        },
        sandbox: agent.sandbox,
      });

      toast.success(`Agent "${agent.name.trim()}" saved.`);
      setDraftAgent(null);
      setEditingId(null);
      await load();
      return resolvedId;
    } catch (err) {
      toast.error(apiError(err), { duration: Infinity });
      return null;
    } finally {
      setSavingId(null);
    }
  };

  const startCreate = () => {
    const defaultModelId = models.length > 0 ? models[0].id : '';
    setDraftAgent({
      id: '',
      scope: 'user',
      enabled: true,
      ownerUserId: '',
      name: '',
      modelId: defaultModelId,
      systemPrompt: '',
      superExecMode: 'disabled',
      mcpBindings: [],
      skillAttachments: [],
      toolExposure: { exposedAsTool: false, description: '', invocationTimeoutSeconds: 120 },
      runtime: {
        maxRunDurationSeconds: null,
        hitlExpirySeconds: null,
        hitlExpiryAction: null,
        allowMultipleToolCalls: null,
        allowConcurrentInvocation: null,
        maxParallelTools: null,
      },
      persona: normalizePersonaProfile(undefined, personaDefinitions),
      session: {
        historyEnabled: true,
        maxHistoryRuns: 5,
        summariesEnabled: true,
        memoryFactExtractionEnabled: true,
        summaryModelId: null,
      },
      sandbox: {
        enabled: false,
        allowedLanguages: [],
        maxMemory: '512m',
        maxTimeout: 30,
      },
    });
    setEditingId('');
  };

  const startEdit = (agentId: string) => {
    setEditingId(agentId);
  };

  const cancelEdit = () => {
    setEditingId(null);
    setDraftAgent(null);
  };

  const removeAgent = async (agentId: string) => {
    const agentName = agents.find((a) => a.id === agentId)?.name || agentId;
    try {
      await settingsAdmin.deleteAgent(agentId);
      toast.success(`Agent "${agentName}" deleted.`);
      await load();
      return true;
    } catch (err) {
      toast.error(apiError(err));
      return false;
    }
  };

  const getAgentToolMode = (agent: AgentSettingsEntry, targetAgentId: string): AgentToolMode => {
    const cfg = normalizeApprovalConfig(agent.approvalConfig);
    if (!cfg.agentToolIds.includes(targetAgentId)) return 'off';
    if (cfg.approvalRequiredAgentToolIds.includes(targetAgentId)) return 'ask';
    return 'on';
  };

  const setAgentToolMode = (agentId: string, targetAgentId: string, mode: AgentToolMode) => {
    patchAgent(agentId, (prev) => {
      const cfg = normalizeApprovalConfig(prev.approvalConfig);
      let agentToolIds = cfg.agentToolIds.filter((id) => id !== targetAgentId);
      let approvalRequiredAgentToolIds = cfg.approvalRequiredAgentToolIds.filter(
        (id) => id !== targetAgentId
      );
      if (mode === 'on') {
        agentToolIds = [...agentToolIds, targetAgentId];
      } else if (mode === 'ask') {
        agentToolIds = [...agentToolIds, targetAgentId];
        approvalRequiredAgentToolIds = [...approvalRequiredAgentToolIds, targetAgentId];
      }
      return {
        ...prev,
        approvalConfig: { ...cfg, agentToolIds, approvalRequiredAgentToolIds },
      };
    });
  };

  const statusItems = useMemo(() => {
    const agentStatus = loading
      ? 'Checking agent runtime profiles...'
      : `${agents.length} agent runtime profile(s) configured`;
    const agentRecent = agents[0]
      ? `Latest agent profile: ${agents[0].id}`
      : 'No agent changes yet';
    const agentSecurity = `${agents.length} agent runtime profile(s) available`;

    return [
      { title: 'Global Status', value: agentStatus },
      { title: 'Recent Activity', value: agentRecent },
      { title: 'Security', value: agentSecurity },
    ];
  }, [loading, agents]);

  return {
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
    getAgentToolMode,
    setAgentToolMode,
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
  };
}

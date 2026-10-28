import { useCurrentUser } from '@/features/auth';
import {
    apiError,
    normalizePersonaDefinitions,
    normalizePersonaProfile,
    settingsAdmin,
    type AgentSettingsEntry,
    type DiscoveredModelEntry,
    type ModelEntry,
    type PersonaDefinitions,
    type ProviderEntry,
} from '@/features/settings';
import { useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
    getGeneratedModelId,
    getGeneratedProviderId,
    getRecommendedProviderModel,
} from '../utils/wizardNaming';
import { createOnboardingHandoffState } from '../utils/onboardingHandoff';
import { useSettingsStatus } from './useSettingsStatus';

const DEFAULT_HTTP_TIMEOUT_SECONDS = '30';

type ProviderTestResult = {
  success: boolean;
  message: string;
};

type ProviderFormState = {
  id: string;
  type: string;
  base_url: string;
  apiCredential: string;
  http_timeout_seconds: string;
};

type ModelFormState = {
  id: string;
  providerId: string;
  modelName: string;
  custom_output_text: string;
};

export const WIZARD_STAGES = {
  welcome: ['wel', 'come'].join(''),
  agentType: 'agent_type',
  setup: 'setup',
  recommendations: 'recommendations',
  telegram: 'telegram',
  success: 'success',
  powerUp: 'power_up',
} as const;

export type WizardStage = (typeof WIZARD_STAGES)[keyof typeof WIZARD_STAGES];

const DEFAULT_AGENT: AgentSettingsEntry = {
  id: '',
  ownerUserId: '',
  name: 'My Assistant',
  systemPrompt: '',
  modelId: '',
  mcpBindings: [],
  skillAttachments: [],
  sandbox: { enabled: true, allowedLanguages: [], maxMemory: '2g', maxTimeout: 120 },
  session: {
    historyEnabled: true,
    maxHistoryRuns: 50,
    summariesEnabled: true,
    memoryFactExtractionEnabled: true,
  },
  superExecMode: 'disabled',
  toolExposure: { exposedAsTool: false, description: '', invocationTimeoutSeconds: 120 },
  persona: {
    temperature: 0.7,
    style: 'professional',
    tone: 'neutral',
    verbosity: 'concise',
  },
};

export function useOnboardingFlow() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { data: currentUser } = useCurrentUser();
  const { data: onboardingStatus } = useSettingsStatus(Boolean(currentUser));
  const [stage, setStage] = useState<WizardStage>(WIZARD_STAGES.welcome);

  // Stable unique IDs per wizard session — prevents overwriting existing records on repeat runs
  const [wizardIds] = useState(() => {
    const ts = Date.now();
    return { agentId: `agent-${ts}` };
  });

  // Data State
  const [providerForm, setProviderForm] = useState<ProviderFormState>(() => ({
    id: '',
    type: 'gemini',
    base_url: '',
    apiCredential: '',
    http_timeout_seconds: DEFAULT_HTTP_TIMEOUT_SECONDS,
  }));
  const [modelForm, setModelForm] = useState<ModelFormState>(() => ({
    id: '',
    providerId: '',
    modelName: getRecommendedProviderModel('gemini'),
    custom_output_text: '',
  }));
  const [agentForm, setAgentForm] = useState<AgentSettingsEntry>(() => ({
    ...DEFAULT_AGENT,
    id: wizardIds.agentId,
    modelId: '',
  }));
  const [selectedAgentPresetId, setSelectedAgentPresetId] = useState<string | null>(null);
  const [selectedBrainPresetId, setSelectedBrainPresetId] = useState<string | null>('gemini-flash');
  const [telegramAccessCode, setTelegramAccessCode] = useState('');
  const [telegramChatId, setTelegramChatId] = useState('');
  const [telegramConfiguredInWizard, setTelegramConfiguredInWizard] = useState(false);

  const [availableModelNames, setAvailableModelNames] = useState<DiscoveredModelEntry[]>([]);
  const [discoveringModels, setDiscoveringModels] = useState(false);
  const [modelCapabilities, setModelCapabilities] = useState<ModelEntry['capabilities']>(null);
  const [confirmedModelWarningKey, setConfirmedModelWarningKey] = useState<string | null>(null);
  const [personaDefinitions, setPersonaDefinitions] = useState<PersonaDefinitions>({
    styles: [],
    tones: [],
    verbosity_levels: [],
  });
  const [providerValidation, setProviderValidation] = useState<ProviderTestResult | null>(null);

  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Existing-config detection — used at WelcomeScreen to offer edit vs create new
  const [checkingExisting, setCheckingExisting] = useState(true);
  const [existingConfig, setExistingConfig] = useState<{
    provider: ProviderEntry;
    model: ModelEntry | null;
    agent: AgentSettingsEntry | null;
  } | null>(null);
  const [existingProviders, setExistingProviders] = useState<ProviderEntry[]>([]);
  const [existingModels, setExistingModels] = useState<ModelEntry[]>([]);
  const canImportConnectors =
    !!currentUser
    && (currentUser.role === 'admin' || Boolean(currentUser.canImportConnectors));
  const canImportSkills =
    !!currentUser
    && (currentUser.role === 'admin' || Boolean(currentUser.canImportSkills));
  const canUseCatalogRecommendations =
    canImportConnectors || canImportSkills;

  useEffect(() => {
    const detectExisting = async () => {
      try {
        const [provRes, modRes, agentRes] = await Promise.all([
          settingsAdmin.listProviders(),
          settingsAdmin.listModels(),
          settingsAdmin.listAgents(),
        ]);
        const providers: ProviderEntry[] = provRes.data.providers;
        const models: ModelEntry[] = modRes.data.models;
        const agents: AgentSettingsEntry[] = agentRes.data.agents;
        const existingSetupAgent = agents[0] ?? null;
        setExistingProviders(providers);
        setExistingModels(models);
        if (providers.length > 0 && existingSetupAgent) {
          const provider = providers[0];
          const model = models.find((m) => m.providerId === provider.id) ?? models[0] ?? null;
          setExistingConfig({ provider, model, agent: existingSetupAgent });
        }
      } catch {
        // silently ignore — treat as no existing config
      }

      setCheckingExisting(false);
    };
    void detectExisting();
  }, []);

  /** Populate forms with existing IDs+data so saves update rather than create new records. */
  const loadExistingSetup = useCallback(() => {
    if (!existingConfig) return;
    const { provider, model, agent } = existingConfig;
    setProviderForm({
      id: provider.id,
      type: provider.type,
      base_url: (provider.settings.base_url as string) ?? '',
      apiCredential: (provider.settings.api_key as string) ?? '',
      http_timeout_seconds: String(provider.settings.http_timeout_seconds ?? DEFAULT_HTTP_TIMEOUT_SECONDS),
    });
    if (model) {
      setModelForm({
        id: model.id,
        providerId: model.providerId,
        modelName: model.modelName,
        custom_output_text: (model.settings?.custom_output_text as string) ?? '',
      });
      setAgentForm((prev) => ({ ...prev, modelId: model.id }));
    }
    if (agent) {
      setAgentForm(agent);
    }
    setSelectedAgentPresetId(null);
    setSelectedBrainPresetId(null);
  }, [existingConfig]);

  useEffect(() => {
    const fetchPersonaDefinitions = async () => {
      try {
        const response = await settingsAdmin.getPersonaDefinitions();
        const defs = normalizePersonaDefinitions(response.data);
        setPersonaDefinitions(defs);
        // Re-normalize agentForm persona now that we have real definitions
        setAgentForm((prev) => ({
          ...prev,
          persona: normalizePersonaProfile(prev.persona, defs),
        }));
      } catch (err) {
        console.error('Failed to fetch persona definitions:', err);
      }
    };
    void fetchPersonaDefinitions();
  }, []);

  useEffect(() => {
    setModelForm((prev) =>
      prev.providerId === providerForm.id ? prev : { ...prev, providerId: providerForm.id }
    );
  }, [providerForm.id]);

  useEffect(() => {
    const recommendedModel = getRecommendedProviderModel(providerForm.type);
    setAvailableModelNames([]);
    setProviderValidation(null);
    setModelForm((prev) => ({
      ...prev,
      id: '',
      modelName: recommendedModel || prev.modelName,
    }));
  }, [providerForm.type]);

  useEffect(() => {
    setAgentForm((prev) =>
      prev.modelId === modelForm.id ? prev : { ...prev, modelId: modelForm.id }
    );
  }, [modelForm.id]);

  useEffect(() => {
    setConfirmedModelWarningKey(null);
  }, [providerForm.type, modelForm.modelName]);

  const discoverModelNames = async (providerId: string, currentModelName?: string) => {
    if (!providerId) {
      setAvailableModelNames([]);
      return;
    }

    setDiscoveringModels(true);
    setError(null);
    try {
      const response = await settingsAdmin.discoverModels({ providerId });
      const discovered: DiscoveredModelEntry[] = response.data.models
        .filter(
          (item): item is DiscoveredModelEntry =>
            typeof item?.id === 'string' && typeof item?.displayName === 'string'
        )
        .map((item) => ({
          id: item.id.trim(),
          displayName: item.displayName.trim(),
          description: item.description?.trim() || null,
          supportsChat: typeof item.supportsChat === 'boolean' ? item.supportsChat : null,
          supportsEmbeddings:
            typeof item.supportsEmbeddings === 'boolean' ? item.supportsEmbeddings : null,
          capabilityTags: Array.isArray(item.capabilityTags)
            ? item.capabilityTags.filter((tag): tag is string => typeof tag === 'string')
            : null,
        }))
        .filter((item) => item.id.length > 0);
      const uniqueNames: DiscoveredModelEntry[] = Array.from(
        new Map(discovered.map((item) => [item.id, item])).values()
      );
      const trimmedCurrentName = currentModelName?.trim();
      if (trimmedCurrentName && !uniqueNames.some((item) => item.id === trimmedCurrentName)) {
        setAvailableModelNames([
          {
            id: trimmedCurrentName,
            displayName: trimmedCurrentName,
            description: null,
            supportsChat: null,
            supportsEmbeddings: null,
            capabilityTags: null,
          },
          ...uniqueNames,
        ]);
      } else {
        setAvailableModelNames(uniqueNames);
      }
    } catch {
      setError(
        'Could not fetch models. Please ensure your provider credentials and URL are correct. You can click "Refresh list" to try again, or type manually.'
      );
      setAvailableModelNames(
        currentModelName?.trim()
          ? [
              {
                id: currentModelName.trim(),
                displayName: currentModelName.trim(),
                description: null,
                supportsChat: null,
                supportsEmbeddings: null,
                capabilityTags: null,
              },
            ]
          : []
      );
    } finally {
      setDiscoveringModels(false);
    }
  };

  const goNext = useCallback((next: WizardStage) => {
    setError(null);
    setStage(next);
  }, []);

  const requiresApiKey = (providerType: string) =>
    ['openai', 'azure-openai', 'gemini', 'google', 'anthropic', 'groq', 'openrouter'].includes(
      providerType
    );

  const requiresBaseUrl = (providerType: string) =>
    ['azure-openai', 'ollama'].includes(providerType);

  const normalizeHttpTimeoutSeconds = (value: string): number | null | 'invalid' => {
    const trimmed = value.trim() || DEFAULT_HTTP_TIMEOUT_SECONDS;

    const parsed = Number(trimmed);
    if (!Number.isFinite(parsed) || parsed <= 0) {
      return 'invalid';
    }

    return parsed;
  };

  const getProviderSettings = (providerType: string) => {
    const trimmedBaseUrl = providerForm.base_url.trim();
    const resolvedBaseUrl =
      providerType === 'ollama' ? trimmedBaseUrl || 'http://localhost:11434' : trimmedBaseUrl;
    const httpTimeoutSeconds = normalizeHttpTimeoutSeconds(providerForm.http_timeout_seconds);

    if (httpTimeoutSeconds === 'invalid') {
      return 'invalid';
    }

    return {
      base_url: resolvedBaseUrl || null,
      api_key: providerForm.apiCredential.trim() || null,
      http_timeout_seconds: httpTimeoutSeconds,
    };
  };

  const saveSetup = async () => {
    const providerId =
      providerForm.id.trim() ||
      getGeneratedProviderId(
        providerForm.type,
        existingProviders.map((provider) => provider.id)
      );
    const providerType = providerForm.type.trim();
    const settings = getProviderSettings(providerType);
    const providerModel = modelForm.modelName.trim() || getRecommendedProviderModel(providerType);

    if (settings === 'invalid') {
      setError('HTTP timeout must be a positive number of seconds.');
      setProviderValidation(null);
      return;
    }

    if (!providerType) {
      setError('Please choose where your helper will run.');
      return;
    }

    if (requiresApiKey(providerType) && !providerForm.apiCredential.trim()) {
      setError('Please paste your provider credential before continuing.');
      setProviderValidation(null);
      return;
    }

    if (requiresBaseUrl(providerType) && !settings.base_url) {
      setError('Please enter the service address before continuing.');
      setProviderValidation(null);
      return;
    }

    if (!agentForm.name.trim()) {
      setError('Agent name is required.');
      return;
    }

    try {
      setSaving(true);
      setError(null);
      setProviderValidation(null);

      const providerTest = await settingsAdmin.testProvider({
        id: providerId,
        type: providerType,
        settings: {
          ...settings,
          default_model: providerModel,
        },
      });

      const validation: ProviderTestResult = {
        success: Boolean(providerTest.data.success),
        message:
          typeof providerTest.data.message === 'string'
            ? providerTest.data.message
            : 'Provider validation failed.',
      };

      setProviderValidation(validation);

      if (!validation.success) {
        setError(validation.message);
        return;
      }

      await settingsAdmin.saveProvider({
        id: providerId,
        type: providerType,
        settings,
      });
      setProviderForm((prev) => ({ ...prev, id: providerId }));
      setExistingProviders((prev) => {
        const nextProvider = {
          id: providerId,
          type: providerType,
          settings: {},
          hasApiKey: Boolean(providerForm.apiCredential.trim()),
        };
        const withoutCurrent = prev.filter((provider) => provider.id !== providerId);
        return [...withoutCurrent, nextProvider];
      });

      const modelName = providerModel;
      const modelId =
        modelForm.id.trim() ||
        getGeneratedModelId(
          providerType,
          modelName,
          existingModels.map((model) => model.id)
        );

      const candidateKey = `${providerId}::${modelName}`;
      const probeResponse = await settingsAdmin.probeModelCandidate({
        providerId,
        modelName,
      });
      const probedCapabilities = probeResponse.data.capabilities ?? null;
      const warningMessage = probeResponse.data.warningMessage?.trim() ?? '';

      setModelCapabilities(null);

      if (warningMessage && confirmedModelWarningKey !== candidateKey) {
        setModelCapabilities(probedCapabilities);
        setConfirmedModelWarningKey(candidateKey);
        setError(`${warningMessage}. Continue again to use this starter model, or choose another provider.`);
        return;
      }

      await settingsAdmin.saveModel({
        id: modelId,
        providerId,
        modelName,
        settings: {},
        capabilities: probedCapabilities,
      });

      setModelCapabilities(probedCapabilities);
      setModelForm((prev) => ({
        ...prev,
        id: modelId,
        providerId,
        modelName,
      }));
      setExistingModels((prev) => {
        const nextModel = {
          id: modelId,
          providerId,
          modelName,
          settings: {},
          capabilities: probedCapabilities,
        };
        const withoutCurrent = prev.filter((model) => model.id !== modelId);
        return [...withoutCurrent, nextModel];
      });

      await settingsAdmin.saveAgent(agentForm.id, {
        name: agentForm.name.trim(),
        modelId,
        systemPrompt: agentForm.systemPrompt,
        superExecMode: agentForm.superExecMode,
        mcpBindings: agentForm.mcpBindings ?? [],
        skillAttachments: agentForm.skillAttachments ?? [],
        persona: normalizePersonaProfile(agentForm.persona, personaDefinitions),
        toolExposure: agentForm.toolExposure,
        session: {
          ...agentForm.session,
          historyEnabled: true,
          maxHistoryRuns: Math.max(1, agentForm.session.maxHistoryRuns),
        },
        sandbox: agentForm.sandbox,
      });

      setAgentForm((prev) => ({
        ...prev,
        modelId,
      }));

      setError(warningMessage ? `${warningMessage}.` : null);
      // Invalidate and wait for the refetch to complete so the guard cache is fresh
      // before the user navigates to /dashboard. Without this, the guard fires with
      // stale providersCount=0 and immediately redirects back to /onboarding.
      await queryClient.invalidateQueries({ queryKey: ['system-setup-status'] });
      await queryClient.refetchQueries({ queryKey: ['system-setup-status'] });
      goNext(WIZARD_STAGES.recommendations);
    } catch (err) {
      setError(apiError(err));
      setProviderValidation(null);
    } finally {
      setSaving(false);
    }
  };

  const saveTelegramSetup = async () => {
    if (!currentUser?.id) {
      goNext(WIZARD_STAGES.success);
      return;
    }

    const trimmedAccessCode = telegramAccessCode.trim();
    const trimmedChatId = telegramChatId.trim();

    if (!trimmedAccessCode || !trimmedChatId) {
      setError('Enter both the Telegram bot access code and private chat ID, or skip this step for now.');
      return;
    }

    try {
      setSaving(true);
      setError(null);
      await settingsAdmin.saveUserChannelSettings(currentUser.id, 'telegram', {
        settings: {
          bot_token: trimmedAccessCode,
          channel_device_id: trimmedChatId,
        },
        defaultTargetType: 'agent',
        defaultTargetId: agentForm.id,
        hitlTetherMode: 'mirror',
      });
      setTelegramAccessCode('');
      setTelegramConfiguredInWizard(true);
      goNext(WIZARD_STAGES.success);
    } catch (err) {
      setError(apiError(err));
    } finally {
      setSaving(false);
    }
  };

  const skipTelegramSetup = () => {
    setError(null);
    goNext(WIZARD_STAGES.success);
  };

  const skipToDashboard = () => {
    navigate('/dashboard', { replace: true, state: createOnboardingHandoffState() });
  };

  const configureManually = () => {
    navigate('/settings?tab=providers', { replace: true });
  };

  const openSettings = () => {
    navigate('/settings');
  };

  return {
    stage,
    goNext,
    onboardingHealth: onboardingStatus?.health,
    saving,
    error,
    checkingExisting,
    hasExistingSetup: existingConfig !== null,
    loadExistingSetup,
    canImportConnectors,
    canImportSkills,
    canUseCatalogRecommendations,
    personaDefinitions,
    selectedAgentPresetId,
    setSelectedAgentPresetId,
    selectedBrainPresetId,
    setSelectedBrainPresetId,
    providerForm,
    setProviderForm,
    saveSetup,
    providerValidation,
    generatedProviderId:
      providerForm.id.trim() ||
      getGeneratedProviderId(
        providerForm.type,
        existingProviders.map((provider) => provider.id)
      ),
    modelForm,
    setModelForm,
    modelCapabilities,
    availableModelNames,
    discoveringModels,
    discoverModelNames,
    generatedModelId:
      modelForm.id.trim() ||
      getGeneratedModelId(
        providerForm.type,
        modelForm.modelName.trim() || getRecommendedProviderModel(providerForm.type),
        existingModels.map((model) => model.id)
      ),
    agentForm,
    setAgentForm,
    telegramAccessCode,
    setTelegramAccessCode,
    telegramChatId,
    setTelegramChatId,
    telegramConfiguredInWizard,
    saveTelegramSetup,
    skipTelegramSetup,
    skipToDashboard,
    configureManually,
    openSettings,
  };
}

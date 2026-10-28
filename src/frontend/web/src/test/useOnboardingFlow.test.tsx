import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import { waitFor } from '@testing-library/dom';
import type { PropsWithChildren } from 'react';
import React from 'react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const {
  listProvidersMock,
  listModelsMock,
  listAgentsMock,
  getPersonaDefinitionsMock,
  testProviderMock,
  saveProviderMock,
  probeModelCandidateMock,
  saveModelMock,
  saveAgentMock,
} = vi.hoisted(() => ({
  listProvidersMock: vi.fn(),
  listModelsMock: vi.fn(),
  listAgentsMock: vi.fn(),
  getPersonaDefinitionsMock: vi.fn(),
  testProviderMock: vi.fn(),
  saveProviderMock: vi.fn(),
  probeModelCandidateMock: vi.fn(),
  saveModelMock: vi.fn(),
  saveAgentMock: vi.fn(),
}));

vi.mock('@/features/auth', () => ({
  useCurrentUser: () => ({
    data: {
      id: 'user-1',
      role: 'admin',
      canImportConnectors: true,
      canImportSkills: true,
    },
  }),
}));

vi.mock('@/features/onboarding/hooks/useSettingsStatus', () => ({
  useSettingsStatus: () => ({
    data: {
      health: {
        lifecycleStatus: 'agents_pending',
        coreReady: true,
        agentsPending: true,
        agentsReady: false,
        actionHints: [],
      },
    },
  }),
}));

vi.mock('@/features/onboarding/utils/wizardNaming', () => ({
  getGeneratedModelId: () => 'generated-model',
  getGeneratedProviderId: () => 'generated-provider',
  getRecommendedProviderModel: () => 'llama-3.3-70b-versatile',
}));

vi.mock('@/features/settings', () => ({
  apiError: (error: unknown) => (error instanceof Error ? error.message : 'unknown error'),
  normalizePersonaDefinitions: (value: unknown) => value,
  normalizePersonaProfile: (value: unknown) => value,
  settingsAdmin: {
    listProviders: listProvidersMock,
    listModels: listModelsMock,
    listAgents: listAgentsMock,
    getPersonaDefinitions: getPersonaDefinitionsMock,
    testProvider: testProviderMock,
    saveProvider: saveProviderMock,
    probeModelCandidate: probeModelCandidateMock,
    saveModel: saveModelMock,
    saveAgent: saveAgentMock,
  },
}));

import { useOnboardingFlow } from '@/features/onboarding/hooks/useOnboardingFlow';

describe('useOnboardingFlow existing setup detection', () => {
  let currentRoute = '';

  beforeEach(() => {
    vi.clearAllMocks();

    listProvidersMock.mockResolvedValue({
      data: {
        providers: [
          {
            id: 'provider-1',
            type: 'groq',
            settings: { base_url: null, api_key: null, http_timeout_seconds: 30 },
            hasApiKey: true,
          },
        ],
      },
    });

    listModelsMock.mockResolvedValue({
      data: {
        models: [
          {
            id: 'model-1',
            providerId: 'provider-1',
            modelName: 'llama-3.3-70b-versatile',
            settings: {},
            capabilities: null,
          },
        ],
      },
    });

    getPersonaDefinitionsMock.mockResolvedValue({
      data: {
        categories: [],
        styles: [],
        tones: [],
        verbosity_levels: [],
      },
    });

    testProviderMock.mockResolvedValue({
      data: {
        success: true,
        message: 'Provider ready.',
      },
    });

    saveProviderMock.mockResolvedValue({ data: {} });
    probeModelCandidateMock.mockResolvedValue({
      data: {
        capabilities: null,
        warningMessage: '',
      },
    });
    saveModelMock.mockResolvedValue({ data: {} });
    saveAgentMock.mockResolvedValue({ data: {} });
    currentRoute = '';
  });

  function LocationCapture() {
    const location = useLocation();
    currentRoute = `${location.pathname}${location.search}`;
    return null;
  }

  function createWrapper() {
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });

    return function Wrapper({ children }: PropsWithChildren) {
      return (
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <LocationCapture />
            {children}
          </MemoryRouter>
        </QueryClientProvider>
      );
    };
  }

  function createAgent(overrides: Partial<Record<string, unknown>> = {}) {
    return {
      id: 'agent-1',
      ownerUserId: 'user-1',
      name: 'Desk Assistant',
      systemPrompt: '',
      modelId: 'model-1',
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
      ...overrides,
    };
  }

  it('treats a configured agent as existing setup', async () => {
    listAgentsMock.mockResolvedValue({
      data: {
        agents: [createAgent()],
      },
    });

    const { result } = renderHook(() => useOnboardingFlow(), { wrapper: createWrapper() });

    await waitFor(() => expect(result.current.checkingExisting).toBe(false));
    expect(result.current.hasExistingSetup).toBe(true);
  });

  it('routes manual setup directly to the provider settings tab', async () => {
    listAgentsMock.mockResolvedValue({ data: { agents: [] } });

    const { result } = renderHook(() => useOnboardingFlow(), { wrapper: createWrapper() });
    await waitFor(() => expect(result.current.checkingExisting).toBe(false));

    await act(async () => result.current.configureManually());

    await waitFor(() => expect(currentRoute).toBe('/settings?tab=providers'));
  });

  it('starts the wizard on the Gemini beginner path by default', async () => {
    listAgentsMock.mockResolvedValue({
      data: {
        agents: [],
      },
    });

    const { result } = renderHook(() => useOnboardingFlow(), { wrapper: createWrapper() });

    await waitFor(() => expect(result.current.checkingExisting).toBe(false));
    expect(result.current.providerForm.type).toBe('gemini');
    expect(result.current.modelForm.modelName).toBe('llama-3.3-70b-versatile');
  });

  it('keeps onboarding choices in memory until the final save runs', async () => {
    listAgentsMock.mockResolvedValue({
      data: {
        agents: [],
      },
    });

    const { result } = renderHook(() => useOnboardingFlow(), { wrapper: createWrapper() });

    await waitFor(() => expect(result.current.checkingExisting).toBe(false));

    await act(async () => {
      result.current.setProviderForm((prev) => ({
        ...prev,
        apiCredential: 'key-123',
      }));
      result.current.setSelectedAgentPresetId('general-helper');
      result.current.setSelectedBrainPresetId('gemini-flash');
    });

    expect(saveProviderMock).not.toHaveBeenCalled();
    expect(saveModelMock).not.toHaveBeenCalled();
    expect(saveAgentMock).not.toHaveBeenCalled();
    expect(result.current.selectedAgentPresetId).toBe('general-helper');
    expect(result.current.selectedBrainPresetId).toBe('gemini-flash');
  });

  it('saves provider, model, and agent only when the final save runs', async () => {
    listAgentsMock.mockResolvedValue({
      data: {
        agents: [],
      },
    });

    const { result } = renderHook(() => useOnboardingFlow(), { wrapper: createWrapper() });

    await waitFor(() => expect(result.current.checkingExisting).toBe(false));

    await act(async () => {
      result.current.setProviderForm((prev) => ({
        ...prev,
        apiCredential: 'key-123',
      }));
    });

    await act(async () => {
      await result.current.saveSetup();
    });

    expect(testProviderMock).toHaveBeenCalledTimes(1);
    expect(saveProviderMock).toHaveBeenCalledTimes(1);
    expect(probeModelCandidateMock).toHaveBeenCalledWith({
      providerId: 'generated-provider',
      modelName: 'llama-3.3-70b-versatile',
    });
    await waitFor(() => expect(saveModelMock).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(saveAgentMock).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(result.current.stage).toBe('recommendations'));
  });

  it('requires a second confirmation when the starter model probe returns a warning', async () => {
    listAgentsMock.mockResolvedValue({
      data: {
        agents: [],
      },
    });

    probeModelCandidateMock.mockResolvedValue({
      data: {
        capabilities: null,
        warningMessage: 'Starter model could be rate limited',
      },
    });

    const { result } = renderHook(() => useOnboardingFlow(), { wrapper: createWrapper() });

    await waitFor(() => expect(result.current.checkingExisting).toBe(false));

    await act(async () => {
      result.current.setProviderForm((prev) => ({
        ...prev,
        apiCredential: 'key-123',
      }));
    });

    await act(async () => {
      await result.current.saveSetup();
    });

    expect(result.current.stage).toBe('welcome');
    expect(result.current.error).toBe(
      'Starter model could be rate limited. Continue again to use this starter model, or choose another provider.'
    );
    expect(saveModelMock).not.toHaveBeenCalled();
    expect(saveAgentMock).not.toHaveBeenCalled();

    await act(async () => {
      await result.current.saveSetup();
    });

    await waitFor(() => expect(saveModelMock).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(saveAgentMock).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(result.current.stage).toBe('recommendations'));
  });
});

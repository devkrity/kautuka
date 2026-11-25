import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import { waitFor } from '@testing-library/dom';
import type { PropsWithChildren } from 'react';
import React from 'react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

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
  createSessionMock,
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
  createSessionMock: vi.fn(),
}));

vi.mock('@/features/chat', () => ({ createSession: createSessionMock }));

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
  getGeneratedProviderId: (_type: string, usedIds: readonly string[]) =>
    usedIds.includes('generated-provider') ? 'replacement-provider' : 'generated-provider',
  getRecommendedProviderModel: () => 'llama-3.3-70b-versatile',
}));

vi.mock('@/features/settings', async () => ({
  ...(await import('@/features/settings/utils/confirmProviderSave')),
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
import { isActiveOnboardingHandoff } from '@/features/onboarding/utils/onboardingHandoff';

describe('useOnboardingFlow existing setup detection', () => {
  let currentRoute = '';
  let currentState: unknown;

  afterEach(() => vi.restoreAllMocks());

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
    currentState = null;
    createSessionMock.mockReset().mockResolvedValue({ id: 'session / 1' });
  });

  function LocationCapture() {
    const location = useLocation();
    currentRoute = `${location.pathname}${location.search}`;
    currentState = location.state;
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

  it('edits the bound helper without touching its model, key, or history', async () => {
    listProvidersMock.mockResolvedValue({ data: { providers: [
      { id: 'unrelated', type: 'gemini', settings: {}, hasApiKey: true },
      { id: 'provider-1', type: 'groq', settings: { http_timeout_seconds: 30 }, hasApiKey: true },
    ] } });
    listModelsMock.mockResolvedValue({ data: { models: [
      { id: 'model-1', providerId: 'provider-1', modelName: 'custom-model', settings: { custom_output_text: 'Preserve' }, capabilities: null },
    ] } });
    const original = createAgent();
    listAgentsMock.mockResolvedValue({ data: { agents: [
      { ...original, session: { ...original.session, historyEnabled: false } },
    ] } });
    const { result } = renderHook(() => useOnboardingFlow(), { wrapper: createWrapper() });

    await waitFor(() => expect(result.current.hasExistingSetup).toBe(true));
    act(() => result.current.loadExistingSetup());
    await waitFor(() => expect(result.current.modelForm.modelName).toBe('custom-model'));
    expect(result.current.providerForm.id).toBe('provider-1');
    expect(result.current.hasSavedProviderCredential).toBe(true);
    act(() => result.current.setAgentForm((previous) => ({ ...previous, name: 'Renamed helper' })));
    await act(async () => result.current.saveSetup());

    expect(saveProviderMock).not.toHaveBeenCalled();
    expect(saveModelMock).not.toHaveBeenCalled();
    expect(saveAgentMock).toHaveBeenCalledWith('agent-1', expect.objectContaining({
      name: 'Renamed helper', modelId: 'model-1', session: expect.objectContaining({ historyEnabled: false }),
    }));
  });

  it('reuses the saved provider after helper setup was interrupted', async () => {
    listAgentsMock.mockResolvedValue({ data: { agents: [] } });
    listModelsMock.mockResolvedValue({ data: { models: [] } });
    const { result } = renderHook(() => useOnboardingFlow(), { wrapper: createWrapper() });

    await waitFor(() => expect(result.current.canResumeSetup).toBe(true));
    act(() => result.current.loadExistingSetup());

    expect(result.current.providerForm.id).toBe('provider-1');
    expect(result.current.hasSavedProviderCredential).toBe(true);
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

    expect(testProviderMock).not.toHaveBeenCalled();
    expect(saveProviderMock).toHaveBeenCalledTimes(1);
    expect(probeModelCandidateMock).toHaveBeenCalledWith({
      providerId: 'generated-provider',
      modelName: 'llama-3.3-70b-versatile',
    });
    await waitFor(() => expect(saveModelMock).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(saveAgentMock).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(result.current.stage).toBe('success'));
    await act(async () => { await result.current.chatWithHelper(); });
    expect(createSessionMock).toHaveBeenCalledWith('agent', result.current.agentForm.id);
    expect(currentRoute).toBe(`/workspace/${encodeURIComponent(result.current.agentForm.id)}?sessionId=session%20%2F%201`);
    expect(isActiveOnboardingHandoff(currentState)).toBe(true);
  });

  it('creates only one chat during rapid clicks and retries after an error', async () => {
    listAgentsMock.mockResolvedValue({ data: { agents: [] } });
    let rejectSession!: (error: Error) => void;
    createSessionMock.mockImplementationOnce(() => new Promise((_resolve, reject) => { rejectSession = reject; }));
    const { result } = renderHook(() => useOnboardingFlow(), { wrapper: createWrapper() });
    await waitFor(() => expect(result.current.checkingExisting).toBe(false));
    let first!: Promise<void>;
    act(() => {
      first = result.current.chatWithHelper();
      void result.current.chatWithHelper();
    });
    expect(createSessionMock).toHaveBeenCalledTimes(1);
    expect(result.current.startingChat).toBe(true);
    await act(async () => { rejectSession(new Error('Could not create conversation')); await first; });
    expect(result.current.error).toBe('Could not create conversation');
    expect(result.current.startingChat).toBe(false);
    expect(currentRoute).toBe('/');
    await act(async () => { await result.current.chatWithHelper(); });
    expect(createSessionMock).toHaveBeenCalledTimes(2);
    expect(currentRoute).toContain('?sessionId=session%20%2F%201');
  });

  it('asks for the exact provider permission before creating the helper', async () => {
    listAgentsMock.mockResolvedValue({ data: { agents: [] } });
    const permission = { destination: 'api.groq.com', port: 443, allowLan: false, revision: '7' };
    saveProviderMock.mockRejectedValueOnce({ response: { status: 409, data: permission } });
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true);

    const { result } = renderHook(() => useOnboardingFlow(), { wrapper: createWrapper() });
    await waitFor(() => expect(result.current.checkingExisting).toBe(false));

    await act(async () => {
      result.current.setProviderForm((prev) => ({ ...prev, apiCredential: 'key-123' }));
    });
    await act(async () => {
      await result.current.saveSetup();
    });

    expect(testProviderMock).not.toHaveBeenCalled();
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('api.groq.com:443'));
    expect(saveProviderMock).toHaveBeenCalledTimes(2);
    expect(saveProviderMock).toHaveBeenLastCalledWith(expect.objectContaining({ networkPermission: permission }));
    expect(saveAgentMock).toHaveBeenCalledTimes(1);
  });

  it('does not prompt when the provider address is already allowed', async () => {
    listAgentsMock.mockResolvedValue({ data: { agents: [] } });

    const { result } = renderHook(() => useOnboardingFlow(), { wrapper: createWrapper() });
    await waitFor(() => expect(result.current.checkingExisting).toBe(false));
    await act(async () => {
      result.current.setProviderForm((prev) => ({ ...prev, apiCredential: 'key-123' }));
    });
    await act(async () => {
      await result.current.saveSetup();
    });

    expect(saveProviderMock).toHaveBeenCalledTimes(1);
    expect(testProviderMock).not.toHaveBeenCalled();
  });

  it('prepares a fresh provider ID after a credential reserves the original ID', async () => {
    listAgentsMock.mockResolvedValue({ data: { agents: [] } });
    saveProviderMock.mockRejectedValueOnce({
      response: { status: 409, data: { code: 'provider_id_reserved' } },
    });
    const { result } = renderHook(() => useOnboardingFlow(), { wrapper: createWrapper() });
    await waitFor(() => expect(result.current.checkingExisting).toBe(false));
    await act(async () => {
      result.current.setProviderForm((prev) => ({ ...prev, apiCredential: 'key-123' }));
    });
    await act(async () => {
      await result.current.saveSetup();
    });

    expect(result.current.providerForm.id).toBe('replacement-provider');
    expect(saveAgentMock).not.toHaveBeenCalled();
    await act(async () => {
      await result.current.saveSetup();
    });
    expect(saveProviderMock).toHaveBeenLastCalledWith(expect.objectContaining({ id: 'replacement-provider' }));
    expect(saveAgentMock).toHaveBeenCalledTimes(1);
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
    await waitFor(() => expect(result.current.stage).toBe('success'));
  });
});

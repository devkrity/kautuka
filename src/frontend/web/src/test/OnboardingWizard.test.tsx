import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { describe, expect, it, vi, beforeEach } from 'vitest';

const flowState = vi.hoisted(() => ({
  stage: 'welcome',
  startingChat: false,
  error: null as string | null,
  chatWithHelper: vi.fn(),
  canManageServiceNetworkAccess: false,
  selectedAgentPresetId: 'general-helper',
  selectedBrainPresetId: 'gemini-flash',
  providerForm: {
    id: '',
    type: 'gemini',
    base_url: '',
    apiCredential: '',
    http_timeout_seconds: '30',
  },
  setProviderForm: vi.fn(),
  setModelForm: vi.fn(),
  goNext: vi.fn(),
  setSelectedAgentPresetId: vi.fn(),
  setSelectedBrainPresetId: vi.fn(),
  saveSetup: vi.fn(),
  configureManually: vi.fn(),
}));

const api = vi.hoisted(() => ({ get: vi.fn() }));
vi.mock('@/lib/apiClient', () => ({ apiClient: api }));

vi.mock('@/features/onboarding/hooks/useOnboardingFlow', () => ({
  WIZARD_STAGES: {
    welcome: 'welcome',
    agentType: 'agent_type',
    setup: 'setup',
    recommendations: 'recommendations',
    telegram: 'telegram',
    success: 'success',
    powerUp: 'power_up',
  },
  useOnboardingFlow: () => ({
    stage: flowState.stage,
    goNext: flowState.goNext,
    skipToDashboard: vi.fn(),
    chatWithHelper: flowState.chatWithHelper,
    startingChat: flowState.startingChat,
    configureManually: flowState.configureManually,
    checkingExisting: false,
    hasExistingSetup: false,
    loadExistingSetup: vi.fn(),
    canImportConnectors: false,
    canImportSkills: false,
    canManageServiceNetworkAccess: flowState.canManageServiceNetworkAccess,
    onboardingHealth: { lifecycleStatus: 'agents_pending', actionHints: [] },
    personaDefinitions: { categories: [], styles: ['professional'], tones: ['neutral'], verbosity_levels: ['concise'] },
    selectedAgentPresetId: flowState.selectedAgentPresetId,
    setSelectedAgentPresetId: flowState.setSelectedAgentPresetId,
    selectedBrainPresetId: flowState.selectedBrainPresetId,
    setSelectedBrainPresetId: flowState.setSelectedBrainPresetId,
    providerForm: flowState.providerForm,
    setProviderForm: flowState.setProviderForm,
    setModelForm: flowState.setModelForm,
    saveSetup: flowState.saveSetup,
    providerValidation: null,
    saving: false,
    error: flowState.error,
    modelForm: { id: '', providerId: '', modelName: 'gemini-3.5-flash-lite', custom_output_text: '' },
    agentForm: { id: 'agent-1', name: 'My Assistant', modelId: '', systemPrompt: '', mcpBindings: [], skillAttachments: [], sandbox: { enabled: true, allowedLanguages: [], maxMemory: '2g', maxTimeout: 120 }, session: { historyEnabled: true, maxHistoryRuns: 50, summariesEnabled: true, memoryFactExtractionEnabled: true }, superExecMode: 'disabled', toolExposure: { exposedAsTool: false, description: '', invocationTimeoutSeconds: 120 }, persona: { temperature: 0.7, style: 'professional', tone: 'neutral', verbosity: 'concise' }, ownerUserId: '' },
    setAgentForm: vi.fn(),
    saveAgent: vi.fn(),
    telegramAccessCode: '',
    setTelegramAccessCode: vi.fn(),
    telegramChatId: '',
    setTelegramChatId: vi.fn(),
    telegramConfiguredInWizard: false,
    saveTelegramSetup: vi.fn(),
    skipTelegramSetup: vi.fn(),
    openSettings: vi.fn(),
  }),
}));

import { OnboardingWizard } from '@/features/onboarding/routes/OnboardingWizard';

describe('OnboardingWizard welcome step', () => {
  beforeEach(() => {
    flowState.stage = 'welcome';
    flowState.startingChat = false;
    flowState.error = null;
    flowState.canManageServiceNetworkAccess = false;
    api.get.mockReset();
    flowState.providerForm = {
      id: '',
      type: 'gemini',
      base_url: '',
      apiCredential: '',
      http_timeout_seconds: '30',
    };
    flowState.setProviderForm.mockReset();
    flowState.setModelForm.mockReset();
    flowState.goNext.mockReset();
    flowState.setSelectedAgentPresetId.mockReset();
    flowState.setSelectedBrainPresetId.mockReset();
    flowState.saveSetup.mockReset();
    flowState.configureManually.mockReset();
  });

  it('shows a session creation failure and busy state on the success route', () => {
    flowState.stage = 'success';
    flowState.startingChat = true;
    flowState.error = 'Could not create conversation';
    render(<OnboardingWizard />);
    expect(screen.getByText('Could not create conversation')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Starting chat…' })).toBeDisabled();
  });

  it('starts on a welcome screen focused on creating a helper', () => {
    render(<OnboardingWizard />);

    expect(screen.getByText('Set up a local AI helper in a few minutes')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Get started' })).toBeInTheDocument();
  });

  it('opens manual provider configuration from the welcome screen', () => {
    render(<OnboardingWizard />);

    fireEvent.click(screen.getByRole('button', { name: "I'll configure this myself" }));

    expect(flowState.configureManually).toHaveBeenCalledOnce();
  });

  it('shows agent presets before setup when the agent type stage is active', () => {
    flowState.stage = 'agent_type';

    render(<OnboardingWizard />);

    expect(screen.getByText('Choose your helper')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /General helper/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Manual setup/i })).toBeInTheDocument();
  });

  it('shows the combined agent and AI brain setup screen', () => {
    flowState.stage = 'setup';

    render(<OnboardingWizard />);

    expect(screen.getByText('Create your helper')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Gemini \+ Gemini Flash/i })).toBeInTheDocument();
    expect(screen.getByText('You can change the brain later, and one provider can serve more than one agent.')).toBeInTheDocument();
  });

  it('requires a new owner to review current service permissions before saving a helper', async () => {
    flowState.stage = 'setup';
    flowState.canManageServiceNetworkAccess = true;
    api.get.mockResolvedValue({
      data: {
        revision: 1,
        rules: [{
          setupService: 'suffix-list', scopeKind: 'agent', userId: 'system', agentId: 'updateservice',
          destination: 'publicsuffix.org', destKind: 'host', port: 443, allowed: true,
        }],
        services: [{ serviceId: 'suffix-list', total: 1, granted: 1, effective: 1 }],
      },
    });
    render(<OnboardingWizard />);

    const confirm = screen.getByRole('button', { name: 'Confirm permissions and continue' });
    expect(confirm).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Save helper' })).toBeNull();
    await waitFor(() => expect(confirm).toBeEnabled());
    expect(screen.getByText('publicsuffix.org')).toBeVisible();
    fireEvent.click(confirm);
    expect(screen.getByRole('button', { name: 'Save helper' })).toBeVisible();
  });

  it('saves the whole setup from the final setup screen', () => {
    flowState.stage = 'setup';

    render(<OnboardingWizard />);

    fireEvent.click(screen.getByRole('button', { name: 'Save helper' }));

    expect(flowState.saveSetup).toHaveBeenCalled();
  });

  it('clears the key and saved binding when switching provider presets', () => {
    flowState.stage = 'setup';
    flowState.providerForm = { ...flowState.providerForm, id: 'saved-gemini', apiCredential: 'fixture-key' };
    render(<OnboardingWizard />);

    fireEvent.click(screen.getByRole('button', { name: /OpenRouter \+ GPT-4.1 Mini/i }));

    const update = flowState.setProviderForm.mock.lastCall?.[0] as
      (previous: typeof flowState.providerForm) => typeof flowState.providerForm;
    expect(update(flowState.providerForm)).toMatchObject({ type: 'openrouter', apiCredential: '', id: '' });
  });
});

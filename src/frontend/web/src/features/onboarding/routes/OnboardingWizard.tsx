import { Alert } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import React from 'react';
import { AgentTypeScreen, type AgentTypePreset } from '../components/AgentTypeScreen';
import { BrainPresetOption, CompactSetupScreen } from '../components/CompactSetupScreen';
import { RecommendationsScreen } from '../components/RecommendationsScreen';
import { SuccessMilestone } from '../components/SuccessMilestone';
import { TelegramSetupScreen } from '../components/TelegramSetupScreen';
import { WIZARD_STAGES, useOnboardingFlow } from '../hooks/useOnboardingFlow';

const AGENT_TYPE_PRESETS: AgentTypePreset[] = [
  {
    id: 'general-helper',
    label: 'General helper',
    description: 'A practical everyday helper for notes, planning, and quick tasks.',
    suggestedName: 'Desk Assistant',
    prompt:
      'You are a practical local assistant. Be clear, concise, and useful. Ask brief follow-up questions only when needed. Prefer concrete next steps, short summaries, and well-structured answers. If something is uncertain, say so plainly.',
    style: 'professional',
    tone: 'neutral',
    verbosity: 'concise',
  },
  {
    id: 'document-reviewer',
    label: 'Document reviewer',
    description: 'Reviews documents for clarity, structure, and missing details.',
    suggestedName: 'Document Reviewer',
    prompt:
      'You review documents for clarity, structure, missing details, and possible inconsistencies. Summarize the main point first, then list issues, open questions, and suggested edits. Do not invent facts that are not present in the document.',
    style: 'analytical',
    tone: 'neutral',
    verbosity: 'concise',
  },
  {
    id: 'news-watcher',
    label: 'News watcher',
    description: 'Tracks what changed and explains why it matters.',
    suggestedName: 'News Watcher',
    prompt:
      'You track and summarize recent developments on a topic. Focus on what changed, why it matters, and what is still uncertain. Keep summaries brief, factual, and easy to scan. Separate verified facts from interpretation.',
    style: 'factual',
    tone: 'neutral',
    verbosity: 'concise',
  },
  {
    id: 'research-analyst',
    label: 'Research analyst',
    description: 'Breaks questions down and compares tradeoffs clearly.',
    suggestedName: 'Research Analyst',
    prompt:
      'You help investigate a topic methodically. Break questions into parts, compare options, identify assumptions, and surface tradeoffs. Prefer evidence-backed reasoning, note gaps explicitly, and finish with a concise conclusion.',
    style: 'analytical',
    tone: 'neutral',
    verbosity: 'moderate',
  },
  {
    id: 'numbers-helper',
    label: 'Numbers helper',
    description: 'Explains figures, comparisons, and trends in plain language.',
    suggestedName: 'Numbers Helper',
    prompt:
      'You help interpret figures, trends, and comparisons. Show the important numbers, explain what they suggest in plain language, and call out uncertainty or missing context. Do not present estimates or calculations as certain when they are approximate.',
    style: 'structured',
    tone: 'neutral',
    verbosity: 'concise',
  },
];

const BRAIN_PRESETS: BrainPresetOption[] = [
  {
    id: 'gemini-flash',
    label: 'Gemini + Gemini Flash',
    summary: 'Easiest cloud start for most first-time users.',
    providerType: 'gemini',
    modelName: 'gemini-3.5-flash-lite',
    helperLinkLabel: 'Open Gemini API key page',
    helperLinkHref: 'https://aistudio.google.com/app/apikey',
  },
  {
    id: 'ollama-gemma4',
    label: 'Ollama + Gemma4',
    summary: 'Local-first option if you want the AI brain on this machine.',
    providerType: 'ollama',
    modelName: 'gemma4:e4b',
    helperLinkLabel: 'Download Ollama',
    helperLinkHref: 'https://ollama.com/download',
  },
  {
    id: 'openrouter-gpt41-mini',
    label: 'OpenRouter + GPT-4.1 Mini',
    summary: 'Flexible cloud option if you want more model choice later.',
    providerType: 'openrouter',
    modelName: 'openai/gpt-4.1-mini',
    helperLinkLabel: 'Open OpenRouter keys',
    helperLinkHref: 'https://openrouter.ai/settings/keys',
  },
];

function getLifecycleSummary(status?: string) {
  if (status === 'agents_ready') {
    return 'Core ready. Agents ready.';
  }

  if (status === 'agents_pending') {
    return 'Core ready. Agents pending setup.';
  }

  return 'Core services are not ready yet.';
}

function getProviderLabel(providerType: string) {
  switch (providerType) {
    case 'gemini':
    case 'google':
      return 'Google Gemini';
    case 'openrouter':
      return 'OpenRouter';
    case 'ollama':
      return 'Ollama';
    default:
      return providerType;
  }
}

function getProviderLink(providerType: string) {
  switch (providerType) {
    case 'gemini':
    case 'google':
      return 'https://aistudio.google.com/app/apikey';
    case 'openrouter':
      return 'https://openrouter.ai/settings/keys';
    case 'ollama':
      return 'https://ollama.com/download';
    default:
      return null;
  }
}

type WizardErrorBoundaryProps = {
  children: React.ReactNode;
};

type WizardErrorBoundaryState = {
  error: Error | null;
};

class WizardErrorBoundary extends React.Component<
  WizardErrorBoundaryProps,
  WizardErrorBoundaryState
> {
  constructor(props: WizardErrorBoundaryProps) {
    super(props);
    this.state = { error: null };
  }

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  componentDidCatch(error: Error, errorInfo: React.ErrorInfo) {
    console.error('WIZARD CRASH CAUGHT BY ERROR BOUNDARY:', error, errorInfo);
  }

  render() {
    if (this.state.error) {
      return (
        <Alert variant="destructive" className="p-8 shadow-sm">
          <h2 className="text-2xl font-bold">Something went wrong</h2>
          <p className="mt-3 text-sm text-destructive/90">
            The setup wizard hit a problem. Please refresh the page and try again. If it keeps
            happening, you can skip setup for now and finish it later in Settings.
          </p>
          <div className="mt-5 flex gap-3">
            <Button onClick={() => globalThis.location.reload()}>Refresh page</Button>
            <Button variant="secondary" onClick={() => globalThis.location.assign('/dashboard')}>
              Go to dashboard
            </Button>
          </div>
        </Alert>
      );
    }
    return this.props.children;
  }
}

export const OnboardingWizard: React.FC = () => {
  const [manualBrainSelection, setManualBrainSelection] = React.useState(false);
  const [recommendationsBackStage, setRecommendationsBackStage] = React.useState<string>(WIZARD_STAGES.setup);
  const {
    stage,
    goNext,
    skipToDashboard,
    configureManually,
    checkingExisting,
    hasExistingSetup,
    loadExistingSetup,
    canImportConnectors,
    canImportSkills,
    onboardingHealth,
    personaDefinitions,
    selectedAgentPresetId,
    setSelectedAgentPresetId,
    selectedBrainPresetId,
    setSelectedBrainPresetId,
    providerForm,
    setProviderForm,
    saveSetup,
    providerValidation,
    saving,
    error,
    modelForm,
    setModelForm,
    agentForm,
    setAgentForm,
    telegramAccessCode,
    setTelegramAccessCode,
    telegramChatId,
    setTelegramChatId,
    telegramConfiguredInWizard,
    saveTelegramSetup,
    skipTelegramSetup,
    openSettings,
  } = useOnboardingFlow();

  const verbosityOptions = personaDefinitions.verbosity_levels.length
    ? personaDefinitions.verbosity_levels
    : ['concise'];
  const styleOptions = personaDefinitions.styles.length ? personaDefinitions.styles : ['professional'];
  const toneOptions = personaDefinitions.tones.length ? personaDefinitions.tones : ['neutral'];
  const requiresApiKey = ['openai', 'azure-openai', 'gemini', 'google', 'anthropic', 'groq', 'openrouter'].includes(providerForm.type);
  const requiresBaseUrl = ['azure-openai', 'ollama'].includes(providerForm.type);

  const applyAgentPreset = (presetId: string | null) => {
    setSelectedAgentPresetId(presetId);

    if (presetId === null) {
      return;
    }

    const preset = AGENT_TYPE_PRESETS.find((item) => item.id === presetId);
    if (!preset) {
      return;
    }

    setAgentForm((prev) => ({
      ...prev,
      name: preset.suggestedName,
      systemPrompt: preset.prompt,
      persona: {
        ...prev.persona,
        style: preset.style,
        tone: preset.tone,
        verbosity: preset.verbosity,
      },
    }));
  };

  const applyBrainPreset = (presetId: string) => {
    const preset = BRAIN_PRESETS.find((item) => item.id === presetId);
    if (!preset) {
      return;
    }

    setSelectedBrainPresetId(presetId);
    setProviderForm((prev) => ({
      ...prev,
      type: preset.providerType,
      base_url: preset.providerType === 'ollama' ? prev.base_url.trim() || 'http://localhost:11434' : '',
    }));
    setModelForm((prev) => ({
      ...prev,
      modelName: preset.modelName,
    }));
  };

  React.useEffect(() => {
    if (selectedBrainPresetId) {
      applyBrainPreset(selectedBrainPresetId);
    }
  }, []);

  return (
    <div className="flex min-h-screen w-full flex-col items-center justify-center bg-background text-foreground p-6">
      <div className="w-full max-w-4xl">
        {error && (
          <Alert variant="destructive" className="mb-6">
            {error}
          </Alert>
        )}

        <WizardErrorBoundary>
          <>
            {stage === WIZARD_STAGES.welcome && (
              <div className="animate-in fade-in slide-in-from-right-8 duration-300">
                <div className="space-y-4">
                  <div>
                    <h1 className="text-4xl font-extrabold tracking-tight text-foreground">
                      Set up a local AI helper in a few minutes
                    </h1>
                    <p className="mt-3 max-w-3xl text-lg text-muted-foreground">
                      Start by choosing the kind of helper you want to create.
                    </p>
                  </div>

                  <div className="w-full rounded-xl border border-border bg-card px-5 py-4 text-sm shadow-sm">
                    <div className="flex items-center justify-between gap-3">
                      <span className="font-semibold text-foreground">Platform status</span>
                      <span className="rounded-full border border-border bg-muted px-3 py-1 text-xs font-semibold text-muted-foreground">
                        {getLifecycleSummary(onboardingHealth?.lifecycleStatus)}
                      </span>
                    </div>
                    {onboardingHealth?.actionHints?.length ? (
                      <p className="mt-3 text-muted-foreground">
                        Suggested next actions: {onboardingHealth.actionHints.join(', ')}
                      </p>
                    ) : null}
                  </div>

                  {checkingExisting ? (
                    <p className="text-sm text-muted-foreground animate-pulse">
                      Checking existing configuration...
                    </p>
                  ) : hasExistingSetup ? (
                    <div className="flex flex-col gap-3 rounded-xl border border-border bg-card p-4 shadow-sm md:flex-row md:items-center md:justify-between">
                      <div>
                        <div className="font-medium text-foreground">A setup already exists</div>
                        <p className="text-sm text-muted-foreground">
                          You can edit it now or create another helper from scratch.
                        </p>
                      </div>
                      <div className="flex gap-3">
                        <Button
                          variant="secondary"
                          onClick={() => {
                            loadExistingSetup();
                            goNext(WIZARD_STAGES.setup);
                          }}
                        >
                          Edit existing setup
                        </Button>
                        <Button variant="ghost" onClick={skipToDashboard}>
                          Skip setup
                        </Button>
                      </div>
                    </div>
                  ) : null}
                </div>

                <div className="flex gap-3 pt-4">
                  <Button size="lg" onClick={() => goNext(WIZARD_STAGES.agentType)}>
                    Get started
                  </Button>
                  <Button size="lg" variant="ghost" onClick={configureManually}>
                    I&apos;ll configure this myself
                  </Button>
                </div>
              </div>
            )}

            {stage === WIZARD_STAGES.agentType && (
              <AgentTypeScreen
                presets={AGENT_TYPE_PRESETS}
                selectedPresetId={selectedAgentPresetId}
                onSelectPreset={applyAgentPreset}
                onBack={() => goNext(WIZARD_STAGES.welcome)}
                onContinue={() => goNext(WIZARD_STAGES.setup)}
              />
            )}

            {stage === WIZARD_STAGES.setup && (
              <CompactSetupScreen
                helperName={agentForm.name}
                onHelperNameChange={(value) => setAgentForm((prev) => ({ ...prev, name: value }))}
                systemPrompt={agentForm.systemPrompt}
                onSystemPromptChange={(value) => setAgentForm((prev) => ({ ...prev, systemPrompt: value }))}
                verbosityOptions={verbosityOptions}
                verbosity={agentForm.persona.verbosity}
                onVerbosityChange={(value) =>
                  setAgentForm((prev) => ({ ...prev, persona: { ...prev.persona, verbosity: value } }))
                }
                styleOptions={styleOptions}
                style={agentForm.persona.style}
                onStyleChange={(value) =>
                  setAgentForm((prev) => ({ ...prev, persona: { ...prev.persona, style: value } }))
                }
                toneOptions={toneOptions}
                tone={agentForm.persona.tone}
                onToneChange={(value) =>
                  setAgentForm((prev) => ({ ...prev, persona: { ...prev.persona, tone: value } }))
                }
                brainPresets={BRAIN_PRESETS}
                selectedBrainPresetId={selectedBrainPresetId}
                onSelectBrainPreset={applyBrainPreset}
                manualBrainSelection={manualBrainSelection}
                onManualBrainSelectionChange={setManualBrainSelection}
                providerType={providerForm.type}
                onProviderTypeChange={(value) => {
                  setSelectedBrainPresetId(null);
                  setProviderForm((prev) => ({
                    ...prev,
                    type: value,
                    base_url: value === 'ollama' ? prev.base_url.trim() || 'http://localhost:11434' : '',
                  }));
                }}
                providerBaseUrl={providerForm.base_url}
                onProviderBaseUrlChange={(value) => setProviderForm((prev) => ({ ...prev, base_url: value }))}
                providerApiCredential={providerForm.apiCredential}
                onProviderApiCredentialChange={(value) =>
                  setProviderForm((prev) => ({ ...prev, apiCredential: value }))
                }
                modelName={modelForm.modelName}
                onModelNameChange={(value) => {
                  setSelectedBrainPresetId(null);
                  setModelForm((prev) => ({ ...prev, modelName: value }));
                }}
                requiresApiKey={requiresApiKey}
                requiresBaseUrl={requiresBaseUrl}
                providerValidationMessage={providerValidation?.message ?? null}
                providerValidationSuccess={providerValidation?.success ?? false}
                runtimeNote="Your AI model can run locally or in the cloud, but Kautuka runs here and performs tool work here."
                relationshipNote="You can change the brain later, and one provider can serve more than one agent."
                saving={saving}
                onBack={() => goNext(WIZARD_STAGES.agentType)}
                onSave={saveSetup}
              />
            )}

            {stage === WIZARD_STAGES.recommendations && (
              <RecommendationsScreen
                canImportConnectors={canImportConnectors}
                canImportSkills={canImportSkills}
                onBack={() => goNext(recommendationsBackStage as typeof stage)}
                onContinue={() => goNext(WIZARD_STAGES.success)}
                onSetupTelegram={() => goNext(WIZARD_STAGES.telegram)}
              />
            )}

            {stage === WIZARD_STAGES.telegram && (
              <TelegramSetupScreen
                agentName={agentForm.name}
                telegramAccessCode={telegramAccessCode}
                telegramChatId={telegramChatId}
                canSave={telegramAccessCode.trim().length > 0 && telegramChatId.trim().length > 0}
                saving={saving}
                onAccessCodeChange={setTelegramAccessCode}
                onChatIdChange={setTelegramChatId}
                onBack={() => goNext(WIZARD_STAGES.recommendations)}
                onSkip={skipTelegramSetup}
                onSave={saveTelegramSetup}
              />
            )}

            {stage === WIZARD_STAGES.success && (
              <SuccessMilestone
                telegramConfigured={telegramConfiguredInWizard}
                onContinue={skipToDashboard}
                onReviewAbilities={() => {
                  setRecommendationsBackStage(WIZARD_STAGES.success);
                  goNext(WIZARD_STAGES.recommendations);
                }}
                onOpenSettings={openSettings}
              />
            )}
          </>
        </WizardErrorBoundary>
      </div>
    </div>
  );
};

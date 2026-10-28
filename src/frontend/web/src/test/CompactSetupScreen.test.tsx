import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { CompactSetupScreen } from '@/features/onboarding/components/CompactSetupScreen';

const brainPresets = [
  {
    id: 'gemini-flash',
    label: 'Gemini + Gemini Flash',
    summary: 'Easy cloud start',
    providerType: 'gemini',
    modelName: 'gemini-3.5-flash-lite',
    helperLinkLabel: 'Open Gemini API key page',
    helperLinkHref: 'https://aistudio.google.com/app/apikey',
  },
  {
    id: 'ollama-gemma4',
    label: 'Ollama + Gemma4',
    summary: 'Local-first option',
    providerType: 'ollama',
    modelName: 'gemma4:e4b',
  },
];

describe('CompactSetupScreen', () => {
  it('shows agent controls before AI brain presets and supporting notes', () => {
    render(
      <CompactSetupScreen
        helperName="Desk Assistant"
        onHelperNameChange={vi.fn()}
        systemPrompt="Review documents"
        onSystemPromptChange={vi.fn()}
        verbosityOptions={['concise', 'moderate']}
        verbosity="concise"
        onVerbosityChange={vi.fn()}
        styleOptions={['professional', 'analytical']}
        style="professional"
        onStyleChange={vi.fn()}
        toneOptions={['neutral', 'friendly']}
        tone="neutral"
        onToneChange={vi.fn()}
        brainPresets={brainPresets}
        selectedBrainPresetId="gemini-flash"
        onSelectBrainPreset={vi.fn()}
        manualBrainSelection={false}
        onManualBrainSelectionChange={vi.fn()}
        providerType="gemini"
        onProviderTypeChange={vi.fn()}
        providerBaseUrl=""
        onProviderBaseUrlChange={vi.fn()}
        providerApiCredential=""
        onProviderApiCredentialChange={vi.fn()}
        modelName="gemini-3.5-flash-lite"
        onModelNameChange={vi.fn()}
        requiresApiKey={true}
        requiresBaseUrl={false}
        providerValidationMessage={null}
        providerValidationSuccess={false}
        runtimeNote="Your AI model can run locally or in the cloud, but Kautuka runs here and performs tool work here."
        relationshipNote="You can change the brain later, and one provider can serve more than one agent."
        saving={false}
        onBack={vi.fn()}
        onSave={vi.fn()}
      />
    );

    expect(screen.getByText('Create your helper')).toBeInTheDocument();
    expect(
      screen.getByText(
        'Your AI model can run locally or in the cloud, but Kautuka runs here and performs tool work here.'
      )
    ).toBeInTheDocument();
    expect(screen.getByPlaceholderText('Desk Assistant')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('Tell the helper what it should focus on first.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Gemini \+ Gemini Flash/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Ollama \+ Gemma4/i })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Open Gemini API key page' })).toBeInTheDocument();
    expect(screen.getByDisplayValue('Desk Assistant')).toBeInTheDocument();
    expect(screen.getByDisplayValue('Review documents')).toBeInTheDocument();
    expect(screen.getByText('You can change the brain later, and one provider can serve more than one agent.')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('Paste your provider key')).toBeInTheDocument();
  });

  it('shows manual brain controls when manual mode is enabled', () => {
    render(
      <CompactSetupScreen
        helperName="Desk Assistant"
        onHelperNameChange={vi.fn()}
        systemPrompt=""
        onSystemPromptChange={vi.fn()}
        verbosityOptions={['concise', 'moderate']}
        verbosity="concise"
        onVerbosityChange={vi.fn()}
        styleOptions={['professional', 'analytical']}
        style="professional"
        onStyleChange={vi.fn()}
        toneOptions={['neutral', 'friendly']}
        tone="neutral"
        onToneChange={vi.fn()}
        brainPresets={brainPresets}
        selectedBrainPresetId={null}
        onSelectBrainPreset={vi.fn()}
        manualBrainSelection={true}
        onManualBrainSelectionChange={vi.fn()}
        providerType="ollama"
        onProviderTypeChange={vi.fn()}
        providerBaseUrl="http://localhost:11434"
        onProviderBaseUrlChange={vi.fn()}
        providerApiCredential=""
        onProviderApiCredentialChange={vi.fn()}
        modelName="gemma4:e4b"
        onModelNameChange={vi.fn()}
        requiresApiKey={false}
        requiresBaseUrl={true}
        providerValidationMessage={null}
        providerValidationSuccess={false}
        runtimeNote="Your AI model can run locally or in the cloud, but Kautuka runs here and performs tool work here."
        relationshipNote="You can change the brain later, and one provider can serve more than one agent."
        saving={false}
        onBack={vi.fn()}
        onSave={vi.fn()}
      />
    );

    expect(screen.getAllByRole('combobox')).toHaveLength(4);
    expect(screen.getByDisplayValue('gemma4:e4b')).toBeInTheDocument();
    expect(screen.getByDisplayValue('http://localhost:11434')).toBeInTheDocument();
  });

  it('offers a manual brain toggle', () => {
    const onManualBrainSelectionChange = vi.fn();

    render(
      <CompactSetupScreen
        helperName="Desk Assistant"
        onHelperNameChange={vi.fn()}
        systemPrompt=""
        onSystemPromptChange={vi.fn()}
        verbosityOptions={['concise']}
        verbosity="concise"
        onVerbosityChange={vi.fn()}
        styleOptions={['professional']}
        style="professional"
        onStyleChange={vi.fn()}
        toneOptions={['neutral']}
        tone="neutral"
        onToneChange={vi.fn()}
        brainPresets={brainPresets}
        selectedBrainPresetId="gemini-flash"
        onSelectBrainPreset={vi.fn()}
        manualBrainSelection={false}
        onManualBrainSelectionChange={onManualBrainSelectionChange}
        providerType="openrouter"
        onProviderTypeChange={vi.fn()}
        providerBaseUrl=""
        onProviderBaseUrlChange={vi.fn()}
        providerApiCredential=""
        onProviderApiCredentialChange={vi.fn()}
        modelName="openai/gpt-4.1-mini"
        onModelNameChange={vi.fn()}
        requiresApiKey={true}
        requiresBaseUrl={false}
        providerValidationMessage={null}
        providerValidationSuccess={false}
        runtimeNote="Your AI model can run locally or in the cloud, but Kautuka runs here and performs tool work here."
        relationshipNote="You can change the brain later, and one provider can serve more than one agent."
        saving={false}
        onBack={vi.fn()}
        onSave={vi.fn()}
      />
    );

    fireEvent.click(screen.getByRole('button', { name: 'Set it up manually' }));

    expect(onManualBrainSelectionChange).toHaveBeenCalledWith(true);
  });
});
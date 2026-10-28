import { ModelForm } from '@/features/settings/components/ModelForm';
import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

describe('ModelForm wizard mode', () => {
  it('uses the provider from the previous step and shows the generated Kautuka model name', () => {
    render(
      <ModelForm
        variant="wizard"
        editingId=""
        form={{
          id: '',
          providerId: 'provider-1',
          modelName: 'gpt-4.1-mini',
          custom_output_text: '',
          contextWindowTokens: '',
          autoCompactThresholdPercent: '',
        }}
        generatedModelId="openai-mini"
        providers={[{ id: 'provider-1', type: 'openai', settings: {}, hasApiKey: true }]}
        availableModelNames={[]}
        discoveringModels={false}
        formCapabilities={null}
        saving={false}
        probingId={null}
        onChange={vi.fn()}
        onSetCapabilities={vi.fn()}
        onCancel={vi.fn()}
        onSave={vi.fn()}
        onProbe={vi.fn()}
        onDiscover={vi.fn()}
      />
    );

    expect(screen.getByText('Using selected provider')).toBeInTheDocument();
    expect(
      screen.getByText("We'll use your openai provider from the previous step.")
    ).toBeInTheDocument();
    expect(
      screen.getByText('For OpenAI, this is usually something like gpt-4o or gpt-4.1-mini.')
    ).toBeInTheDocument();
    expect(
      screen.getByPlaceholderText(
        'Choose the exact model from that provider, like gpt-4o or gpt-4.1-mini'
      )
    ).toBeInTheDocument();
    expect(screen.getByText('Kautuka name')).toBeInTheDocument();
    expect(screen.getByText('openai-mini')).toBeInTheDocument();
    expect(
      screen.getByText('You only need to choose the real model. We will name it cleanly for you.')
    ).toBeInTheDocument();
    expect(screen.queryByText('Provider')).not.toBeInTheDocument();
    expect(screen.queryByDisplayValue('openai-mini')).not.toBeInTheDocument();
  });

  it('shows provider display names while keeping model ids as select values', () => {
    render(
      <ModelForm
        editingId=""
        form={{
          id: '',
          providerId: 'provider-1',
          modelName: 'gemini-3.5-flash-lite',
          custom_output_text: '',
          contextWindowTokens: '',
          autoCompactThresholdPercent: '',
        }}
        generatedModelId="gemini-flash-lite"
        providers={[{ id: 'provider-1', type: 'gemini', settings: {}, hasApiKey: true }]}
        availableModelNames={[
          {
            id: 'gemini-3.5-flash-lite',
            displayName: 'Gemini 3.1 Flash Lite',
            description: 'Higher free-tier prompt headroom',
          },
        ]}
        discoveringModels={false}
        formCapabilities={null}
        saving={false}
        probingId={null}
        onChange={vi.fn()}
        onSetCapabilities={vi.fn()}
        onCancel={vi.fn()}
        onSave={vi.fn()}
        onProbe={vi.fn()}
        onDiscover={vi.fn()}
      />
    );

    expect(screen.getByRole('option', { name: 'Gemini 3.1 Flash Lite' })).toHaveValue(
      'gemini-3.5-flash-lite'
    );
    expect(
      screen.queryByRole('option', { name: 'gemini-3.5-flash-lite' })
    ).not.toBeInTheDocument();
  });
});

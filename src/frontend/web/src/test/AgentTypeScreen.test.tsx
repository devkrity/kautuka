import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { AgentTypeScreen } from '@/features/onboarding/components/AgentTypeScreen';

const presets = [
  {
    id: 'general-helper',
    label: 'General helper',
    description: 'A practical everyday helper.',
    suggestedName: 'Desk Assistant',
    prompt: 'General helper prompt',
    style: 'professional',
    tone: 'neutral',
    verbosity: 'concise',
  },
  {
    id: 'document-reviewer',
    label: 'Document reviewer',
    description: 'Reviews documents for clarity.',
    suggestedName: 'Document Reviewer',
    prompt: 'Document reviewer prompt',
    style: 'analytical',
    tone: 'neutral',
    verbosity: 'concise',
  },
];

describe('AgentTypeScreen', () => {
  it('shows helper presets with persona cues and a manual option', () => {
    render(
      <AgentTypeScreen
        presets={presets}
        selectedPresetId="general-helper"
        onSelectPreset={vi.fn()}
        onBack={vi.fn()}
        onContinue={vi.fn()}
      />
    );

    expect(screen.getByText('Choose your helper')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /General helper/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Document reviewer/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Manual setup/i })).toBeInTheDocument();
    expect(screen.getAllByText('concise')).toHaveLength(2);
    expect(screen.getByText('professional')).toBeInTheDocument();
  });

  it('lets the user select manual setup', () => {
    const onSelectPreset = vi.fn();

    render(
      <AgentTypeScreen
        presets={presets}
        selectedPresetId={null}
        onSelectPreset={onSelectPreset}
        onBack={vi.fn()}
        onContinue={vi.fn()}
      />
    );

    fireEvent.click(screen.getByRole('button', { name: /Manual setup/i }));

    expect(onSelectPreset).toHaveBeenCalledWith(null);
  });
});
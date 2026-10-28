import { Tabs } from '@/components/ui/tabs';
import { AgentPersonaEditor } from '@/features/settings/components/AgentPersonaEditor';
import type { AgentSettingsEntry, PersonaDefinitions } from '@/features/settings';
import { fireEvent, screen } from '@testing-library/dom';
import { render } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

const definitions: PersonaDefinitions = {
  styles: ['professional', 'helper'],
  tones: ['neutral', 'friendly'],
  verbosity_levels: ['concise', 'normal'],
};

function agent(): AgentSettingsEntry {
  return {
    id: 'agent-1',
    ownerUserId: '',
    name: 'Agent One',
    modelId: 'model-1',
    systemPrompt: '',
    superExecMode: 'disabled',
    mcpBindings: [],
    skillAttachments: [],
    persona: { temperature: 0.7, style: 'professional', tone: 'neutral', verbosity: 'concise' },
    session: {
      historyEnabled: true,
      maxHistoryRuns: 5,
      summariesEnabled: false,
      summaryModelId: null,
      memoryFactExtractionEnabled: true,
    },
    sandbox: { enabled: true, allowedLanguages: [], maxMemory: '2g', maxTimeout: 120 },
    toolExposure: { exposedAsTool: false, description: '', invocationTimeoutSeconds: 120 },
  };
}

describe('AgentPersonaEditor', () => {
  it('owns persona mutations and clamps response variety', () => {
    let current = agent();
    const onPatch = vi.fn((update: (previous: AgentSettingsEntry) => AgentSettingsEntry) => {
      current = update(current);
    });
    render(
      <Tabs defaultValue="persona">
        <AgentPersonaEditor agent={current} definitions={definitions} onPatch={onPatch} />
      </Tabs>
    );

    fireEvent.change(screen.getByLabelText('Response variety'), { target: { value: '1.5' } });
    fireEvent.change(screen.getByLabelText('Style'), {
      target: { value: 'helper' },
    });

    expect(current.persona.temperature).toBe(1);
    expect(current.persona.style).toBe('helper');
    expect(onPatch).toHaveBeenCalledTimes(2);
  });
});

import type { AgentSettingsEntry, ModelEntry, PersonaDefinitions } from '@/features/settings';
import { AgentForm } from '@/features/settings';
import type { Skill } from '@/features/skills';
import { screen } from '@testing-library/dom';
import { render, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

function createAgent(): AgentSettingsEntry {
  return {
    id: 'agent-1',
    ownerUserId: '',
    name: 'Agent One',
    modelId: 'model-1',
    systemPrompt: 'Be helpful',
    superExecMode: 'disabled',
    mcpBindings: [],
    skillAttachments: [],
    persona: {
      temperature: 0.7,
      style: 'professional',
      tone: 'neutral',
      verbosity: 'concise',
    },
    session: {
      historyEnabled: true,
      maxHistoryRuns: 5,
      summariesEnabled: false,
      summaryModelId: null,
      memoryFactExtractionEnabled: true,
    },
    sandbox: {
      enabled: true,
      allowedLanguages: [],
      maxMemory: '2g',
      maxTimeout: 120,
    },
    toolExposure: { exposedAsTool: false, description: '', invocationTimeoutSeconds: 120 },
  };
}

const models: ModelEntry[] = [
  {
    id: 'model-1',
    providerId: 'provider-1',
    modelName: 'gemma',
    settings: {},
    capabilities: null,
  },
];

const personaDefinitions: PersonaDefinitions = {
  styles: ['professional', 'helper'],
  tones: ['neutral', 'friendly'],
  verbosity_levels: ['concise', 'normal'],
};

const skills: Skill[] = [];

describe('AgentForm persona controls', () => {
  it('does not render office metadata persona fields', async () => {
    let current = createAgent();

    const onPatch = vi.fn((updater: (prev: AgentSettingsEntry) => AgentSettingsEntry) => {
      current = updater(current);
    });

    render(
      <AgentForm
        agent={current}
        agents={[]}
        models={models}
        personaDefinitions={personaDefinitions}
        mcpServers={[]}
        skills={skills}
        isToolPanelOpen={() => false}
        getConnectorMode={() => 'off'}
        getToolMode={() => 'on'}
        getAgentToolMode={() => 'off'}
        onPatch={onPatch}
        onModeChange={() => undefined}
        onTogglePanel={() => undefined}
        onToolModeChange={() => undefined}
        onAgentToolModeChange={() => undefined}
      />
    );

    await userEvent.click(screen.getByRole('tab', { name: 'Persona' }));

    expect(screen.queryByLabelText('Title (optional)')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Department (optional)')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Industry (optional)')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Company name (optional)')).not.toBeInTheDocument();
    expect(onPatch).not.toHaveBeenCalled();
  });

  it('describes retained-session semantics without a history toggle', () => {
    render(
      <AgentForm
        agent={createAgent()}
        agents={[]}
        models={models}
        personaDefinitions={personaDefinitions}
        mcpServers={[]}
        skills={skills}
        isToolPanelOpen={() => false}
        getConnectorMode={() => 'off'}
        getToolMode={() => 'on'}
        getAgentToolMode={() => 'off'}
        onPatch={() => undefined}
        onModeChange={() => undefined}
        onTogglePanel={() => undefined}
        onToolModeChange={() => undefined}
        onAgentToolModeChange={() => undefined}
      />
    );

    expect(screen.queryByText('Session history enabled')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Retained sessions')).toBeInTheDocument();
    expect(
      screen.getByText(
        'Current-session context is always preserved. This controls how many older sessions are retained after a new session starts.'
      )
    ).toBeInTheDocument();
  });

  it('shows configured model ids in the picker while keeping model ids as values', () => {
    render(
      <AgentForm
        agent={createAgent()}
        agents={[]}
        models={models}
        personaDefinitions={personaDefinitions}
        mcpServers={[]}
        skills={skills}
        isToolPanelOpen={() => false}
        getConnectorMode={() => 'off'}
        getToolMode={() => 'on'}
        getAgentToolMode={() => 'off'}
        onPatch={() => undefined}
        onModeChange={() => undefined}
        onTogglePanel={() => undefined}
        onToolModeChange={() => undefined}
        onAgentToolModeChange={() => undefined}
      />
    );

    const modelSelect = screen.getByLabelText('Model');
    const summarySelect = screen.getByLabelText('Summary model');
    const modelOption = within(summarySelect).getByRole('option', { name: 'model-1' });

    expect(modelSelect).toHaveValue('model-1');
    expect(modelOption).toHaveValue('model-1');
    expect(screen.queryByRole('option', { name: 'gemma' })).not.toBeInTheDocument();
  });

  it('shows the previously selected model as read-only in wizard mode', () => {
    render(
      <AgentForm
        variant="wizard"
        agent={createAgent()}
        agents={[]}
        models={models}
        personaDefinitions={personaDefinitions}
        mcpServers={[]}
        skills={skills}
        isToolPanelOpen={() => false}
        getConnectorMode={() => 'off'}
        getToolMode={() => 'on'}
        getAgentToolMode={() => 'off'}
        onPatch={() => undefined}
        onModeChange={() => undefined}
        onTogglePanel={() => undefined}
        onToolModeChange={() => undefined}
        onAgentToolModeChange={() => undefined}
      />
    );

    expect(
      screen.getByText('Using the model you picked in the previous step.')
    ).toBeInTheDocument();
    expect(screen.getByText('model-1')).toBeInTheDocument();
    expect(screen.queryByText('gemma')).not.toBeInTheDocument();
    expect(screen.getByText('Sandbox & Tools')).toBeInTheDocument();
    expect(
      screen.getByText(
        'Your agent starts with a secure sandbox available for tool-based work, but no extra tools are turned on here in the wizard. Later, in Settings, you can choose which tools and connectors this agent is allowed to use.'
      )
    ).toBeInTheDocument();
    expect(screen.getByText('Approvals & Review')).toBeInTheDocument();
    expect(
      screen.getByText(
        'If you want this agent to ask before doing certain sensitive actions, you can set that up later in Settings. When approval is required, the request will appear in the Approvals area for someone to review.'
      )
    ).toBeInTheDocument();
    expect(screen.getByText('Connectors')).toBeInTheDocument();
    expect(
      screen.getByText(
        'Connectors let your agent use external tools and integrations, like services, APIs, or workspace actions. Later, open the Connectors page to add or enable connectors, then go to Settings for this agent to choose which connectors and tools it can use.'
      )
    ).toBeInTheDocument();
    expect(screen.getByText('Skills')).toBeInTheDocument();
    expect(
      screen.getByText(
        'Skills are reusable packaged capabilities that teach an agent how to handle a kind of task. Later, open the Skills page to import the skills you want, then return to this agent in Settings and turn on the skills it should use.'
      )
    ).toBeInTheDocument();
    expect(screen.queryByRole('combobox', { name: 'Model' })).not.toBeInTheDocument();
  });

  it('explains and disables nested agent-tool selection when the agent is exposed as a tool', async () => {
    const current = {
      ...createAgent(),
      toolExposure: { exposedAsTool: true, description: 'Reusable helper', invocationTimeoutSeconds: 120 },
    };

    render(
      <AgentForm
        agent={current}
        agents={[
          current,
          {
            ...createAgent(),
            id: 'agent-2',
            name: 'Agent Two',
            toolExposure: {
              exposedAsTool: true,
              description: 'Nested helper',
              invocationTimeoutSeconds: 120,
            },
          },
        ]}
        models={models}
        personaDefinitions={personaDefinitions}
        mcpServers={[]}
        skills={skills}
        isToolPanelOpen={() => false}
        getConnectorMode={() => 'off'}
        getToolMode={() => 'on'}
        getAgentToolMode={() => 'off'}
        onPatch={() => undefined}
        onModeChange={() => undefined}
        onTogglePanel={() => undefined}
        onToolModeChange={() => undefined}
        onAgentToolModeChange={() => undefined}
      />
    );

    expect(screen.getByText(/Expose this agent as a tool/)).toBeInTheDocument();
    expect(screen.getByText('Experimental')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('tab', { name: 'Connectors' }));

    expect(
      screen.getByText(
        'This agent is exposed as a tool, so it cannot invoke other agents as tools. Clear any existing agent tool selections or disable Expose as tool before saving.'
      )
    ).toBeInTheDocument();
    const agentToolRow = screen.getByText('Agent Two').closest('.rounded-xl');

    expect(agentToolRow).not.toBeNull();
    expect(within(agentToolRow as HTMLElement).getByRole('button', { name: 'Trusted' })).toBeDisabled();
    expect(
      within(agentToolRow as HTMLElement).getByRole('button', { name: 'Ask first' })
    ).toBeDisabled();
    expect(within(agentToolRow as HTMLElement).getByRole('button', { name: 'Disabled' })).toBeEnabled();
  });
});

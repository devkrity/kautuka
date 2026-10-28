import type { AgentSettingsEntry, ModelEntry, PersonaDefinitions } from '@/features/settings';
import { AgentForm } from '@/features/settings';
import type { Connector } from '@/features/connectors';
import type { Skill } from '@/features/skills';
import type { AgentToolPolicyProjection } from '@/features/settings';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, screen } from '@testing-library/dom';
import { render } from '@testing-library/react';
import type { ReactNode } from 'react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

vi.mock('@/features/approvals', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/features/approvals')>();
  return {
    ...actual,
    HitlFunctionStat: () => null,
    useHitlStats: () => ({ data: { windowDays: 30, since: '', items: [] }, isLoading: false }),
    useHitlDeniedCalls: () => ({ data: [], isLoading: false }),
  };
});

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
  styles: ['professional'],
  tones: ['neutral'],
  verbosity_levels: ['concise'],
};

const skills: Skill[] = [
  {
    id: 'repo-inspector',
    name: 'Repo Inspector',
    description: 'Inspect repository contents',
    packageHash: 'hash-1',
    status: 'ready',
    packagePath: 'skills/repo-inspector',
    hasResources: true,
    hasScripts: true,
    enabled: true,
    envKeys: [],
    importTimestamp: '2026-04-18T00:00:00Z',
    sourceMetadata: {},
    scope: 'system',
    ownerUserId: null,
  },
];

const connectors: Connector[] = [
  {
    id: 'remote-web-tools',
    name: 'Kautuka Web Tools Remote',
    description: 'Remote MCP',
    iconUrl: '',
    presetId: null,
    transport: 'http',
    httpUrl: 'https://example.invalid/mcp',
    containerImage: null,
    containerCommand: null,
    containerArgs: [],
    containerSidecarName: null,
    bundlePath: null,
    serverType: null,
    readinessTimeoutSeconds: null,
    jitCooldownSeconds: null,
    healthcheckUrl: null,
    healthcheckIntervalMilliseconds: null,
    mcpProtocolVersion: null,
    enabled: true,
    isInternalSystemConnector: false,
    toolCount: 1,
    envKeys: [],
    toolNames: ['fetch'],
    scope: 'system',
    ownerUserId: null,
    createdAt: '2026-05-01T00:00:00Z',
    updatedAt: '2026-05-01T00:00:00Z',
  },
  {
    id: 'bundle-web-tools',
    name: 'Kautuka Web Tools Bundle',
    description: 'Bundle MCP',
    iconUrl: '',
    presetId: null,
    transport: 'container',
    httpUrl: null,
    containerImage: 'ghcr.io/example/web-tools:latest',
    containerCommand: null,
    containerArgs: [],
    containerSidecarName: null,
    bundlePath: '/var/lib/kautuka/bundles/web-tools.mcpb',
    serverType: 'node',
    readinessTimeoutSeconds: null,
    jitCooldownSeconds: null,
    healthcheckUrl: null,
    healthcheckIntervalMilliseconds: null,
    mcpProtocolVersion: null,
    enabled: true,
    isInternalSystemConnector: false,
    toolCount: 1,
    envKeys: [],
    toolNames: ['search'],
    scope: 'system',
    ownerUserId: null,
    createdAt: '2026-05-01T00:00:00Z',
    updatedAt: '2026-05-01T00:00:00Z',
  },
  {
    id: 'filesystem',
    name: 'Filesystem',
    description: 'Read local files',
    iconUrl: '',
    presetId: null,
    transport: 'container',
    httpUrl: null,
    containerImage: 'ghcr.io/example/filesystem:latest',
    containerCommand: null,
    containerArgs: [],
    containerSidecarName: null,
    bundlePath: null,
    serverType: null,
    readinessTimeoutSeconds: null,
    jitCooldownSeconds: null,
    healthcheckUrl: null,
    healthcheckIntervalMilliseconds: null,
    mcpProtocolVersion: null,
    enabled: true,
    isInternalSystemConnector: false,
    toolCount: 1,
    envKeys: [],
    toolNames: ['read_file'],
    scope: 'system',
    ownerUserId: null,
    createdAt: '2026-05-01T00:00:00Z',
    updatedAt: '2026-05-01T00:00:00Z',
  },
];

function renderWithQueryClient(node: ReactNode) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
    },
  });

  return render(<QueryClientProvider client={queryClient}>{node}</QueryClientProvider>);
}

describe('AgentForm skills controls', () => {
  it('renders imported skills and updates attachment mode', async () => {
    let current = createAgent();

    const onPatch = vi.fn((updater: (prev: AgentSettingsEntry) => AgentSettingsEntry) => {
      current = updater(current);
    });

    renderWithQueryClient(
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

    await userEvent.click(screen.getByRole('tab', { name: 'Skills' }));

    expect(screen.getByText('Repo Inspector')).toBeInTheDocument();
    fireEvent.click(screen.getByTitle('Ask first for Repo Inspector'));

    expect(onPatch).toHaveBeenCalled();
    expect(current.skillAttachments).toEqual([
      { skillId: 'repo-inspector', mode: 'require_approval' },
    ]);
  });

  it('shows an empty-state message when no skills are available', async () => {
    renderWithQueryClient(
      <AgentForm
        agent={createAgent()}
        agents={[]}
        models={models}
        personaDefinitions={personaDefinitions}
        mcpServers={[]}
        skills={[]}
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

    await userEvent.click(screen.getByRole('tab', { name: 'Skills' }));

    expect(
      screen.getByText('No platform skills imported yet. Import them from the Skills page first.')
    ).toBeInTheDocument();
  });

  it('groups connectors and updates bundle execution context', async () => {
    let current = {
      ...createAgent(),
      mcpBindings: [{ connectorId: 'bundle-web-tools', context: 'OwnContext' }],
    };

    const onPatch = vi.fn((updater: (prev: AgentSettingsEntry) => AgentSettingsEntry) => {
      current = updater(current);
    });

    renderWithQueryClient(
      <AgentForm
        agent={current}
        agents={[]}
        models={models}
        personaDefinitions={personaDefinitions}
        mcpServers={connectors}
        skills={skills}
        isToolPanelOpen={() => false}
        getConnectorMode={(connectorId) => (connectorId === 'bundle-web-tools' ? 'on' : 'off')}
        getToolMode={() => 'on'}
        getAgentToolMode={() => 'off'}
        onPatch={onPatch}
        onModeChange={() => undefined}
        onTogglePanel={() => undefined}
        onToolModeChange={() => undefined}
        onAgentToolModeChange={() => undefined}
      />
    );

    await userEvent.click(screen.getByRole('tab', { name: 'Connectors' }));
    expect(screen.getByText('Remote MCPs')).toBeInTheDocument();
    expect(screen.getByText('MCP Bundles')).toBeInTheDocument();
    expect(screen.getByText('Container MCPs')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Within Agent Sandbox' }));

    expect(onPatch).toHaveBeenCalled();
    expect(current.mcpBindings).toEqual([{ connectorId: 'bundle-web-tools', context: 'AgentContext' }]);
  });

  it('disables agent sandbox execution context when sandbox is disabled', async () => {
    let current: AgentSettingsEntry = {
      ...createAgent(),
      sandbox: {
        ...createAgent().sandbox,
        enabled: false,
      },
      mcpBindings: [{ connectorId: 'bundle-web-tools', context: 'OwnContext' }],
    };

    const onPatch = vi.fn((updater: (prev: AgentSettingsEntry) => AgentSettingsEntry) => {
      current = updater(current);
    });

    renderWithQueryClient(
      <AgentForm
        agent={current}
        agents={[]}
        models={models}
        personaDefinitions={personaDefinitions}
        mcpServers={connectors}
        skills={skills}
        isToolPanelOpen={() => false}
        getConnectorMode={(connectorId) => (connectorId === 'bundle-web-tools' ? 'on' : 'off')}
        getToolMode={() => 'on'}
        getAgentToolMode={() => 'off'}
        onPatch={onPatch}
        onModeChange={() => undefined}
        onTogglePanel={() => undefined}
        onToolModeChange={() => undefined}
        onAgentToolModeChange={() => undefined}
      />
    );

    await userEvent.click(screen.getByRole('tab', { name: 'Connectors' }));

    const sandboxOption = screen.getByRole('button', { name: 'Within Agent Sandbox' });
    expect(sandboxOption).toBeDisabled();

    await userEvent.click(sandboxOption);

    expect(onPatch).not.toHaveBeenCalled();
    expect(current.mcpBindings).toEqual([{ connectorId: 'bundle-web-tools', context: 'OwnContext' }]);
  });

  it('disables skill controls when sandbox is disabled', async () => {
    const disabledSandboxAgent: AgentSettingsEntry = {
      ...createAgent(),
      sandbox: {
        ...createAgent().sandbox,
        enabled: false,
      },
    };

    renderWithQueryClient(
      <AgentForm
        agent={disabledSandboxAgent}
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

    await userEvent.click(screen.getByRole('tab', { name: 'Skills' }));

    expect(screen.getByText('Skills disabled (sandbox required).')).toBeInTheDocument();
    expect(screen.getByText('Enable sandbox to attach and run skills.')).toBeInTheDocument();

    expect(screen.getByTitle('Trusted for Repo Inspector')).toBeDisabled();
    expect(screen.getByTitle('Ask first for Repo Inspector')).toBeDisabled();
  });

  it('prefers local sandbox state over stale tool policy projection for skills', async () => {
    const disabledSandboxAgent: AgentSettingsEntry = {
      ...createAgent(),
      sandbox: {
        ...createAgent().sandbox,
        enabled: false,
      },
    };

    const staleProjection: AgentToolPolicyProjection = {
      agentId: disabledSandboxAgent.id,
      categories: [
        {
          category: 'skill',
          exposure: {
            allowed: true,
            reasonCode: 'allowed',
            message: 'Allowed by stale saved policy.',
          },
          execution: {
            allowed: true,
            reasonCode: 'allowed',
            message: 'Allowed by stale saved policy.',
          },
        },
      ],
    };

    renderWithQueryClient(
      <AgentForm
        agent={disabledSandboxAgent}
        agents={[]}
        models={models}
        personaDefinitions={personaDefinitions}
        toolPolicyProjection={staleProjection}
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

    await userEvent.click(screen.getByRole('tab', { name: 'Skills' }));

    expect(screen.getByText('Skills disabled (sandbox required).')).toBeInTheDocument();
    expect(screen.getByTitle('Trusted for Repo Inspector')).toBeDisabled();
  });
});

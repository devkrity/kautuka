import { Tabs } from '@/components/ui/tabs';
import type { Connector } from '@/features/connectors';
import { AgentConnectorsEditor } from '@/features/settings/components/AgentConnectorsEditor';
import type { AgentSettingsEntry } from '@/features/settings/types/settingsTypes';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

vi.mock('@/features/connectors', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/features/connectors')>();
  return {
    ...actual,
    useConnectorOAuth: () => ({ data: undefined, isLoading: false }),
    useConnectorCredentials: () => ({ data: undefined, isLoading: false }),
  };
});

function createAgent(overrides: Partial<AgentSettingsEntry> = {}): AgentSettingsEntry {
  return {
    id: 'agent-1',
    name: 'Family Assistant',
    modelId: 'model-1',
    ownerUserId: 'owner-1',
    systemPrompt: '',
    superExecMode: 'disabled',
    mcpBindings: [
      { connectorId: 'secret-bundle', context: 'OwnContextWithAgentFiles' },
      { connectorId: 'plain-bundle', context: 'OwnContext' },
    ],
    skillAttachments: [],
    persona: { temperature: 0.3, style: 'default', tone: 'friendly', verbosity: 'normal' },
    toolExposure: { exposedAsTool: false, description: '', invocationTimeoutSeconds: 60 },
    notices: [
      "Connector Secret Bundle now runs separately for safety. It can still read your agent's files but can't change them.",
    ],
    session: {
      historyEnabled: true,
      maxHistoryRuns: 5,
      summariesEnabled: true,
      memoryFactExtractionEnabled: true,
    },
    sandbox: {
      enabled: true,
      allowedLanguages: [],
      maxMemory: '512m',
      maxTimeout: 30,
    },
    ...overrides,
  };
}

function createBundleConnector(id: string, name: string, envKeys: string[]): Connector {
  return {
    id,
    name,
    description: '',
    iconUrl: '',
    presetId: null,
    transport: 'container',
    httpUrl: null,
    containerImage: 'example/bundle:latest',
    containerCommand: 'node',
    containerArgs: ['server.js'],
    containerSidecarName: null,
    bundlePath: `/bundles/${id}.mcpb`,
    serverType: 'node',
    readinessTimeoutSeconds: 30,
    jitCooldownSeconds: 600,
    healthcheckUrl: null,
    healthcheckIntervalMilliseconds: null,
    mcpProtocolVersion: null,
    enabled: true,
    isInternalSystemConnector: false,
    toolCount: 1,
    toolNames: ['read_notes'],
    envKeys,
    requiresCredentialReentry: false,
    usesCredentials: envKeys.length > 0,
    scope: 'user',
    ownerUserId: 'owner-1',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
  };
}

describe('AgentConnectorsEditor credentialed connector execution context', () => {
  it('shows owner safety notices and omits Within Agent Sandbox for credentialed connectors while keeping it for plain bundles', () => {
    const agent = createAgent();
    const connectors = [
      createBundleConnector('secret-bundle', 'Secret Bundle', ['API_TOKEN']),
      createBundleConnector('plain-bundle', 'Plain Bundle', []),
    ];

    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <Tabs value="connectors">
          <AgentConnectorsEditor
            agent={agent}
            agents={[agent]}
            connectors={connectors}
            selectionEnabled
            selectionMessage=""
            isToolPanelOpen={() => true}
            getConnectorMode={() => 'on'}
            getToolMode={() => 'on'}
            getAgentToolMode={() => 'off'}
            onPatch={vi.fn()}
            onModeChange={vi.fn()}
            onTogglePanel={vi.fn()}
            onToolModeChange={vi.fn()}
            onAgentToolModeChange={vi.fn()}
          />
        </Tabs>
      </QueryClientProvider>
    );

    expect(
      screen.getByText(
        "Connector Secret Bundle now runs separately for safety. It can still read your agent's files but can't change them."
      )
    ).toBeInTheDocument();
    expect(
      screen.getByText(/This connector uses sign-in or secret keys, so it runs separately for safety/i)
    ).toBeInTheDocument();

    // Only the plain bundle offers "Within Agent Sandbox"; the credentialed bundle omits it.
    expect(screen.getAllByRole('button', { name: 'Within Agent Sandbox' })).toHaveLength(1);
    expect(
      screen.getAllByRole('button', { name: 'Isolated with access to Agent Files' })
    ).toHaveLength(2);
  });
});

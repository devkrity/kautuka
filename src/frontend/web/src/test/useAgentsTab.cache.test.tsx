import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import { waitFor } from '@testing-library/dom';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { settingsQueryKeys } from '@/features/settings/api/queryKeys';
import { useAgentsTab } from '@/features/settings/hooks/useAgentsTab';

const {
  listAgentsMock,
  listModelsMock,
  getPersonaDefinitionsMock,
  getOrganizationMock,
  getSystemSettingsMock,
  getRuntimeLimitsMock,
  saveAgentMock,
  fetchConnectorsMock,
  fetchSkillsMock,
  toastSuccessMock,
  toastErrorMock,
} = vi.hoisted(() => ({
  listAgentsMock: vi.fn(),
  listModelsMock: vi.fn(),
  getPersonaDefinitionsMock: vi.fn(),
  getOrganizationMock: vi.fn(),
  getSystemSettingsMock: vi.fn(),
  getRuntimeLimitsMock: vi.fn(),
  saveAgentMock: vi.fn(),
  fetchConnectorsMock: vi.fn(),
  fetchSkillsMock: vi.fn(),
  toastSuccessMock: vi.fn(),
  toastErrorMock: vi.fn(),
}));

vi.mock('@/features/settings', () => ({
  settingsAdmin: {
    listAgents: listAgentsMock,
    listModels: listModelsMock,
    getPersonaDefinitions: getPersonaDefinitionsMock,
    getOrganization: getOrganizationMock,
    getSystemSettings: getSystemSettingsMock,
    getRuntimeLimits: getRuntimeLimitsMock,
    saveAgent: saveAgentMock,
  },
}));

vi.mock('@/features/connectors', async () => {
  const actual =
    await vi.importActual<typeof import('@/features/connectors')>('@/features/connectors');
  return {
    ...actual,
    fetchConnectors: fetchConnectorsMock,
  };
});

vi.mock('@/features/skills', async () => {
  const actual = await vi.importActual<typeof import('@/features/skills')>('@/features/skills');
  return {
    ...actual,
    fetchSkills: fetchSkillsMock,
  };
});

vi.mock('sonner', () => ({
  toast: {
    success: toastSuccessMock,
    error: toastErrorMock,
  },
}));

describe('useAgentsTab query cache sync', () => {
  beforeEach(() => {
    vi.clearAllMocks();

    listAgentsMock
      .mockResolvedValueOnce({
        data: {
          agents: [
            {
              id: 'agent-1',
              name: 'Alpha',
              modelId: 'model-1',
              ownerUserId: 'user-1',
              systemPrompt: '',
              superExecMode: 'disabled',
              mcpBindings: [],
              skillAttachments: [],
              approvalConfig: undefined,
              persona: {
                category: 'general',
                style: 'balanced',
                tone: 'neutral',
                verbosity: 'concise',
                icon: '🔎',
                color: '#2563EB',
              },
              toolExposure: {
                exposedAsTool: false,
                description: '',
                invocationTimeoutSeconds: 120,
              },
              session: {
                historyEnabled: true,
                maxHistoryRuns: 5,
                summariesEnabled: true,
                summaryModelId: null,
              },
              sandbox: {
                enabled: false,
                allowedLanguages: [],
                maxMemory: '512m',
                maxTimeout: 30,
              },
            },
          ],
        },
      })
      .mockResolvedValueOnce({
        data: {
          agents: [
            {
              id: 'agent-1',
              name: 'Alpha Prime',
              modelId: 'model-1',
              ownerUserId: 'user-1',
              systemPrompt: '',
              superExecMode: 'disabled',
              mcpBindings: [],
              skillAttachments: [],
              approvalConfig: undefined,
              persona: {
                category: 'general',
                style: 'balanced',
                tone: 'neutral',
                verbosity: 'concise',
                icon: '🔎',
                color: '#2563EB',
              },
              toolExposure: {
                exposedAsTool: false,
                description: '',
                invocationTimeoutSeconds: 120,
              },
              session: {
                historyEnabled: true,
                maxHistoryRuns: 5,
                summariesEnabled: true,
                summaryModelId: null,
              },
              sandbox: {
                enabled: true,
                allowedLanguages: [],
                maxMemory: '1g',
                maxTimeout: 45,
              },
            },
          ],
        },
      });

    listModelsMock.mockResolvedValue({
      data: {
        models: [{ id: 'model-1', providerId: 'provider-1', modelName: 'gpt' }],
      },
    });

    getPersonaDefinitionsMock.mockResolvedValue({
      data: {
        definitions: {
          styles: ['balanced'],
          tones: ['neutral'],
          verbosity_levels: ['concise'],
        },
      },
    });

    getOrganizationMock.mockResolvedValue({
      data: {
        organization: {
          name: 'Contoso',
          industry: 'Software',
        },
      },
    });

    getSystemSettingsMock.mockRejectedValue(new Error('admin settings are not part of agent authoring'));
    getRuntimeLimitsMock.mockResolvedValue({
      data: {
        modelTimeoutSeconds: 77,
        maxRunDurationSeconds: 0,
        hitlExpirySeconds: 0,
        hitlExpiryAction: 'abort',
        allowMultipleToolCalls: true,
        allowConcurrentInvocation: false,
        maxParallelTools: 0,
      },
    });

    saveAgentMock.mockResolvedValue({ data: {} });
    fetchConnectorsMock.mockResolvedValue([]);
    fetchSkillsMock.mockResolvedValue([]);
  });

  it('publishes refreshed agents into the shared agents query after save', async () => {
    const client = new QueryClient({
      defaultOptions: {
        queries: { retry: false },
      },
    });

    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );

    const { result } = renderHook(() => useAgentsTab(), { wrapper });

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.models.map((model) => model.id)).toEqual(['model-1']);
    expect(result.current.platformRuntime.modelTimeoutSeconds).toBe(77);
    expect(getSystemSettingsMock).not.toHaveBeenCalled();

    await act(async () => {
      await result.current.saveAgent({
        ...result.current.agents[0],
        name: 'Alpha Prime',
        sandbox: {
          ...result.current.agents[0].sandbox,
          enabled: true,
          maxMemory: '1g',
          maxTimeout: 45,
        },
      });
    });

    await waitFor(() => {
      expect(client.getQueryData(settingsQueryKeys.agents())).toEqual([
        {
          id: 'agent-1',
          name: 'Alpha Prime',
          scope: 'user',
          ownerUserId: 'user-1',
          enabled: true,
          modelId: 'model-1',
          sandbox: {
            enabled: true,
            allowedLanguages: [],
            maxMemory: '1g',
            maxTimeout: 45,
          },
          persona: {
            icon: '🔎',
            color: '#2563EB',
          },
        },
      ]);
    });

    expect(saveAgentMock).toHaveBeenCalledWith(
      'agent-1',
      expect.objectContaining({ name: 'Alpha Prime' })
    );
  });
});

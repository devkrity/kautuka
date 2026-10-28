import type { AgentSettingsEntry, ModelEntry, PersonaDefinitions } from '@/features/settings';
import { AgentForm } from '@/features/settings';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

const { apiClientGetMock, apiClientPutMock } = vi.hoisted(() => ({
  apiClientGetMock: vi.fn(),
  apiClientPutMock: vi.fn(),
}));

vi.mock('@/lib/apiClient', () => ({
  apiClient: {
    get: apiClientGetMock,
    put: apiClientPutMock,
  },
}));

vi.mock('@/features/approvals', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/features/approvals')>();
  return {
    ...actual,
    HitlFunctionStat: () => null,
    useHitlStats: () => ({ data: { windowDays: 30, since: '', items: [] }, isLoading: false }),
    useHitlDeniedCalls: () => ({ data: [], isLoading: false }),
  };
});

const accountChannel = {
  providerKey: 'telegram',
  registrationId: 'telegram:user-1:default',
  externalUserId: '998877',
  status: 'ACTIVE',
  settings: { channel_device_id: '998877' },
  configuredSecretFieldKeys: ['bot_token'],
};

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

function renderForm() {
  return render(
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
}

async function openChannels() {
  await userEvent.click(screen.getByRole('tab', { name: 'Channels' }));
  expect(await screen.findByRole('button', { name: 'Account bot' })).toBeInTheDocument();
}

describe('agent channels tab', () => {
  it('keeps the agent on the account bot until a separate bot is chosen', async () => {
    apiClientGetMock.mockResolvedValue({ data: { channels: [accountChannel] } });
    renderForm();
    await openChannels();

    expect(screen.getByRole('button', { name: 'Account bot' }).className).toContain('bg-background');
    expect(screen.getByText('This agent uses your account bot, so you can switch between agents in one Telegram chat with Kautuka.')).toBeInTheDocument();
    expect(screen.queryByLabelText('Bot token')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument();
  });

  it('disables a separate bot until the account has a device ID', async () => {
    apiClientGetMock.mockResolvedValue({ data: { channels: [] } });
    renderForm();
    await openChannels();

    expect(screen.getByText('Set a device ID in your user settings before giving this agent its own bot.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Separate bot' })).toBeDisabled();
    expect(screen.queryByLabelText('Bot token')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument();
  });

  it('asks for the bot token and keeps the account chat', async () => {
    apiClientGetMock.mockResolvedValue({ data: { channels: [accountChannel] } });
    apiClientPutMock.mockResolvedValue({
      data: {
        channels: [
          accountChannel,
          {
            providerKey: 'telegram',
            registrationId: 'telegram:user-1:agent:agent-1',
            externalUserId: '998877',
            status: 'ACTIVE',
            settings: { channel_device_id: '998877' },
            configuredSecretFieldKeys: ['bot_token'],
          },
        ],
      },
    });
    renderForm();
    await openChannels();

    await userEvent.click(screen.getByRole('button', { name: 'Separate bot' }));
    expect(screen.getByText('This agent will answer through a separate Telegram bot chat. Start that bot in Telegram before using it. Choose Account bot if you want all agents in the same chat.')).toBeInTheDocument();
    expect(screen.queryByLabelText('Device ID')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Different device' })).not.toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Bot token'), { target: { value: 'token-from-botfather' } });
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(apiClientPutMock).toHaveBeenCalled());
    expect(apiClientPutMock).toHaveBeenCalledWith('/settings/me/channels/telegram', {
      settings: {
        channel_device_id: '998877',
        bot_token: 'token-from-botfather',
      },
      attachmentRole: 'agent',
      attachmentSubject: 'agent-1',
      clear: false,
    });
  });

  it('saves the account chat id with the bot token', async () => {
    apiClientGetMock.mockResolvedValue({ data: { channels: [accountChannel] } });
    apiClientPutMock.mockResolvedValue({ data: { channels: [accountChannel] } });
    renderForm();
    await openChannels();

    await userEvent.click(screen.getByRole('button', { name: 'Separate bot' }));
    expect(screen.queryByLabelText('Device ID')).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Bot token'), { target: { value: 'other-token' } });
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() =>
      expect(apiClientPutMock).toHaveBeenCalledWith(
        '/settings/me/channels/telegram',
        expect.objectContaining({
          settings: { channel_device_id: '998877', bot_token: 'other-token' },
          clear: false,
        })
      )
    );
  });
});

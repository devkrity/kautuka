import React from 'react';
import { render, within } from '@testing-library/react';
import { screen, waitFor } from '@testing-library/dom';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { AutopilotPage } from '@/pages/AutopilotPage';

const {
  useAutopilotDataMock,
  useAgentsMock,
  useAgentFilesMock,
  useSearchAgentFilesMock,
  useWorkflowMock,
  useUploadAgentFileMock,
  runNowMock,
} = vi.hoisted(() => ({
  useAutopilotDataMock: vi.fn(),
  useAgentsMock: vi.fn(),
  useAgentFilesMock: vi.fn(),
  useSearchAgentFilesMock: vi.fn(),
  useWorkflowMock: vi.fn(),
  useUploadAgentFileMock: vi.fn(),
  runNowMock: vi.fn(),
}));

vi.mock('@/features/autopilot', async () => {
  const actual =
    await vi.importActual<typeof import('@/features/autopilot')>('@/features/autopilot');
  return {
    ...actual,
    useAutopilotData: useAutopilotDataMock,
  };
});

vi.mock('@/features/settings', async () => {
  const actual = await vi.importActual<typeof import('@/features/settings')>('@/features/settings');
  return {
    ...actual,
    useAgents: useAgentsMock,
  };
});

vi.mock('@/features/agent-files', async () => {
  const actual =
    await vi.importActual<typeof import('@/features/agent-files')>('@/features/agent-files');
  return {
    ...actual,
    useAgentFiles: useAgentFilesMock,
    useSearchAgentFiles: useSearchAgentFilesMock,
  };
});

vi.mock('@/features/agent-files/api/queries', () => ({
  useAgentFiles: useAgentFilesMock,
  useSearchAgentFiles: useSearchAgentFilesMock,
  useUploadAgentFile: useUploadAgentFileMock,
  useCreateAgentFolder: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useDeleteAgentFileEntry: () => ({ mutateAsync: vi.fn(), isPending: false }),
}));

vi.mock('@/features/workflows', async () => {
  const actual =
    await vi.importActual<typeof import('@/features/workflows')>('@/features/workflows');
  return {
    ...actual,
    useWorkflow: useWorkflowMock,
    useWorkflows: () => ({ data: [{ id: 'wf-1', name: 'Weekly review' }] }),
  };
});

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <AutopilotPage />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

describe('AutopilotPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useWorkflowMock.mockReturnValue({ data: undefined, error: null });
    useUploadAgentFileMock.mockReturnValue({ mutateAsync: vi.fn(), isPending: false });
    useAgentsMock.mockReturnValue({
      data: [
        {
          id: 'agent-1',
          name: 'Planner',
        },
      ],
    });
    useAgentFilesMock.mockReturnValue({
      data: {
        status: 'ready',
        response: {
          agentId: 'agent-1',
          path: '',
          entries: [],
          totalFileCount: 0,
          totalSizeBytes: 0,
        },
      },
    });
    useSearchAgentFilesMock.mockImplementation((_agentId: string | null, query: string) => ({
      data: {
        agentId: 'agent-1',
        query,
        entries: query.toLowerCase().startsWith('rea')
          ? [
              {
                name: 'README.md',
                path: 'README.md',
                entryType: 'file',
                sizeBytes: 128,
                modifiedAt: new Date().toISOString(),
                fileCount: null,
              },
            ]
          : [],
      },
    }));
    useAutopilotDataMock.mockReturnValue({
      schedules: [
        {
          id: 'asched-1',
          ownerUserId: 'user-1',
          targetType: 'agent',
          targetId: 'agent-1',
          cronExpression: '*/5 * * * *',
          timeZoneId: 'UTC',
          mode: 'on',
          requiresPreRunApproval: false,
          schedulePromptContext: 'check status',
          notifyUiInbox: true,
          notifyChannels: false,
          isPaused: false,
          createdAt: new Date().toISOString(),
          updatedAt: new Date().toISOString(),
          nextDueAt: new Date().toISOString(),
        },
      ],
      runs: [],
      inbox: [],
      loading: false,
      error: null,
      draft: {
        targetType: 'agent',
        targetId: '',
        cronExpression: '*/5 * * * *',
        timeZoneId: 'UTC',
        mode: 'on',
        requiresPreRunApproval: false,
        schedulePromptContext: '',
        notifyUiInbox: true,
        notifyChannels: false,
      },
      setDraft: vi.fn(),
      editingScheduleId: null,
      setEditingScheduleId: vi.fn(),
      setDraftFromSchedule: vi.fn(),
      save: vi.fn(),
      pauseSchedule: vi.fn(),
      resumeSchedule: vi.fn(),
      runNow: runNowMock,
      deleteSchedule: vi.fn(),
      markInboxRead: vi.fn(),
      saving: false,
      mutating: false,
    });
  });

  it('renders schedules and triggers run-now action', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByText('Planner')).toBeInTheDocument());

    await userEvent.click(screen.getByRole('button', { name: /run now/i }));
    await waitFor(() => expect(runNowMock).toHaveBeenCalledWith('asched-1'));
  });

  it('queries workspace file suggestions from the prompt like chat', async () => {
    const saveMock = vi.fn().mockResolvedValue(true);

    useAutopilotDataMock.mockImplementation(() => {
      const [draft, setDraft] = React.useState({
        targetType: 'agent' as const,
        targetId: '',
        cronExpression: '*/5 * * * *',
        mode: 'on' as const,
        schedulePromptContext: '',
        workspaceFilePaths: [],
        notifyUiInbox: true,
        notifyChannels: false,
      });

      return {
        schedules: [],
        runs: [],
        inbox: [],
        loading: false,
        error: null,
        draft,
        setDraft,
        editingScheduleId: null,
        setEditingScheduleId: vi.fn(),
        setDraftFromSchedule: vi.fn(),
        save: saveMock,
        pauseSchedule: vi.fn(),
        resumeSchedule: vi.fn(),
        runNow: vi.fn(),
        deleteSchedule: vi.fn(),
        markInboxRead: vi.fn(),
        saving: false,
        mutating: false,
      };
    });

    renderPage();

    await userEvent.click(screen.getByRole('button', { name: /create new schedule/i }));
    await userEvent.selectOptions(screen.getByTitle('Select schedule target'), 'agent-1');

    const prompt = screen.getByPlaceholderText(
      'e.g. "Summarize recent support tickets and flag anything urgent"'
    );
    await userEvent.type(prompt, 'Review #rea');

    await waitFor(() => expect(prompt).toHaveValue('Review #rea'));
    await waitFor(() =>
      expect(useSearchAgentFilesMock).toHaveBeenLastCalledWith('agent-1', 'rea', 8)
    );
  });

  it('disables Choose files until a target is selected', async () => {
    renderPage();
    await userEvent.click(screen.getByRole('button', { name: /create new schedule/i }));
    expect(screen.getByRole('button', { name: 'Choose files' })).toBeDisabled();
    expect(screen.getByText('Choose a target first.')).toBeInTheDocument();
  });

  it('uses the workspace picker without opening the OS picker and supports removing selected chips', async () => {
    const user = userEvent.setup();
    useAgentFilesMock.mockReturnValue({
      data: {
        status: 'ready',
        response: {
          agentId: 'agent-1',
          path: '',
          entries: [
            {
              name: 'summary.txt',
              path: 'reports/summary.txt',
              entryType: 'file',
              sizeBytes: 42,
              modifiedAt: new Date().toISOString(),
              fileCount: null,
            },
          ],
          totalFileCount: 1,
          totalSizeBytes: 42,
        },
      },
      isPending: false,
      isFetching: false,
      error: null,
      refetch: vi.fn(),
    });
    useAutopilotDataMock.mockImplementation(() => {
      const [draft, setDraft] = React.useState({
        targetType: 'agent' as const,
        targetId: 'agent-1',
        cronExpression: '*/5 * * * *',
        mode: 'on' as const,
        schedulePromptContext: '',
        workspaceFilePaths: [] as string[],
        notifyUiInbox: true,
        notifyChannels: false,
      });
      return {
        schedules: [],
        runs: [],
        inbox: [],
        loading: false,
        error: null,
        draft,
        setDraft,
        editingScheduleId: null,
        setEditingScheduleId: vi.fn(),
        setDraftFromSchedule: vi.fn(),
        save: vi.fn().mockResolvedValue(true),
        pauseSchedule: vi.fn(),
        resumeSchedule: vi.fn(),
        runNow: vi.fn(),
        deleteSchedule: vi.fn(),
        markInboxRead: vi.fn(),
        saving: false,
        mutating: false,
      };
    });

    renderPage();
    await user.click(screen.getByRole('button', { name: /create new schedule/i }));
    await user.selectOptions(screen.getByLabelText('Which agent?'), 'agent-1');
    const osPickerClick = vi.spyOn(HTMLInputElement.prototype, 'click');
    await user.click(screen.getByRole('button', { name: 'Choose files' }));
    expect(osPickerClick).not.toHaveBeenCalled();
    expect(
      await screen.findByRole('heading', { name: 'Choose files from Planner’s workspace' })
    ).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Use this file: summary.txt' }));
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Close' }));
    expect(screen.getByText('reports/summary.txt')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Remove reports/summary.txt' }));
    expect(screen.queryByText('reports/summary.txt')).not.toBeInTheDocument();
    osPickerClick.mockRestore();
  });

  it('uses the workflow first-step agent for the picker and # references', async () => {
    const user = userEvent.setup();
    useWorkflowMock.mockReturnValue({
      data: {
        id: 'wf-1',
        name: 'Weekly review',
        inputSchema: { properties: { topic: { type: 'string' } }, required: [] },
        nodes: [{ agentId: 'agent-1' }],
      },
      error: null,
    });
    useSearchAgentFilesMock.mockImplementation((_agentId: string | null, query: string) => ({
      data: {
        agentId: 'agent-1',
        query,
        entries:
          query === 'rea'
            ? [
                {
                  name: 'README.md',
                  path: 'README.md',
                  entryType: 'file',
                  sizeBytes: 128,
                  modifiedAt: new Date().toISOString(),
                  fileCount: null,
                },
              ]
            : [],
      },
    }));
    useAutopilotDataMock.mockImplementation(() => {
      const [draft, setDraft] = React.useState({
        targetType: 'workflow' as const,
        targetId: 'wf-1',
        cronExpression: '*/5 * * * *',
        mode: 'on' as const,
        schedulePromptContext: '{}',
        workspaceFilePaths: [] as string[],
        notifyUiInbox: true,
        notifyChannels: false,
      });
      return {
        schedules: [],
        runs: [],
        inbox: [],
        loading: false,
        error: null,
        draft,
        setDraft,
        editingScheduleId: null,
        setEditingScheduleId: vi.fn(),
        setDraftFromSchedule: vi.fn(),
        save: vi.fn().mockResolvedValue(true),
        pauseSchedule: vi.fn(),
        resumeSchedule: vi.fn(),
        runNow: vi.fn(),
        deleteSchedule: vi.fn(),
        markInboxRead: vi.fn(),
        saving: false,
        mutating: false,
      };
    });

    renderPage();
    await user.click(screen.getByRole('button', { name: /create new schedule/i }));
    await user.selectOptions(screen.getByLabelText('What should run?'), 'workflow');
    await user.selectOptions(screen.getByLabelText('Which workflow?'), 'wf-1');
    await user.click(screen.getByRole('button', { name: 'Choose files' }));
    await waitFor(() => expect(useAgentFilesMock).toHaveBeenCalledWith('agent-1', ''));
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Close' }));

    const topic = screen.getByLabelText('Topic');
    await user.type(topic, '#rea');
    expect(await screen.findByRole('button', { name: /#README\.md/ })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /#README\.md/ }));
    await waitFor(() => expect(topic).toHaveValue('#README.md '));
    expect(useSearchAgentFilesMock).toHaveBeenCalledWith('agent-1', 'rea', 8);
  });
});

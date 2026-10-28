import { AgentFilesPanel, WorkspaceFilePickerDialog } from '@/features/agent-files';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import axios from 'axios';
import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const { useAgentFilesMock, useCreateAgentFolderMock, useDeleteAgentFileEntryMock, useUploadAgentFileMock, downloadAgentFileMock, fetchConversationAssetBlobMock } = vi.hoisted(() => ({
  useAgentFilesMock: vi.fn(),
  useCreateAgentFolderMock: vi.fn(),
  useDeleteAgentFileEntryMock: vi.fn(),
  useUploadAgentFileMock: vi.fn(),
  downloadAgentFileMock: vi.fn(),
  fetchConversationAssetBlobMock: vi.fn(),
}));

vi.mock('@/features/chat', () => ({
  fetchConversationAssetBlob: fetchConversationAssetBlobMock,
}));

vi.mock('@/features/agent-files/api/queries', () => ({
  useAgentFiles: useAgentFilesMock,
  useCreateAgentFolder: useCreateAgentFolderMock,
  useDeleteAgentFileEntry: useDeleteAgentFileEntryMock,
  useUploadAgentFile: useUploadAgentFileMock,
}));

vi.mock('@/features/agent-files/api/agentFiles', () => ({
  downloadAgentFile: downloadAgentFileMock,
}));

vi.mock('sonner', () => ({
  toast: {
    success: vi.fn(),
    error: vi.fn(),
  },
}));

describe('AgentFilesPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.spyOn(globalThis, 'confirm').mockReturnValue(true);
    vi.spyOn(axios, 'isAxiosError').mockReturnValue(false);
    useCreateAgentFolderMock.mockReturnValue({
      mutateAsync: vi.fn().mockResolvedValue({ name: 'drafts' }),
      isPending: false,
    });
    useDeleteAgentFileEntryMock.mockReturnValue({
      mutateAsync: vi.fn().mockResolvedValue(undefined),
      isPending: false,
    });
    useUploadAgentFileMock.mockReturnValue({
      mutateAsync: vi.fn().mockResolvedValue({ fileName: 'summary.txt' }),
      isPending: false,
    });
  });

  it('shows the disabled-state message while keeping the files surface visible', () => {
    useAgentFilesMock.mockReturnValue({
      data: {
        status: 'disabled',
        response: {
          agentId: 'agent-1',
          reasonCode: 'sandbox_disabled',
          message: 'Agent file exchange is unavailable because the sandbox is disabled.',
        },
      },
      isPending: false,
      isFetching: false,
      error: null,
      refetch: vi.fn(),
    });

    render(
      <AgentFilesPanel
        agent={{
          id: 'agent-1',
          name: 'Planner',
          sandboxEnabled: false,
        }}
        conversationAssets={[{
          id: 'asset-1',
          fileName: 'place.jpg',
          contentType: 'image/jpeg',
          sizeBytes: 2048,
          origin: 'userUpload',
          disposition: 'inlinePreview',
          downloadUrl: '/api/sessions/session-1/assets/asset-1',
          createdAt: '2026-07-18T00:16:25Z',
        }]}
      />
    );

    expect(screen.getByText('Files')).toBeInTheDocument();
    expect(screen.getByText('Files unavailable')).toBeInTheDocument();
    expect(
      screen.getByText('Agent file exchange is unavailable because the sandbox is disabled.')
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Upload' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Create folder' })).toBeDisabled();
    expect(screen.getByText('Conversation attachments')).toBeInTheDocument();
    expect(screen.getByText('place.jpg')).toBeInTheDocument();
  });

  it('navigates into directories and downloads files from the current view', async () => {
    const user = userEvent.setup();

    useAgentFilesMock.mockImplementation((agentId: string | null, path: string) => ({
      data: {
        status: 'ready',
        response: {
          agentId: agentId ?? 'agent-1',
          path,
          entries:
            path === 'reports'
              ? [
                  {
                      name: 'summary.txt',
                      path: 'reports/summary.txt',
                      entryType: 'file',
                      sizeBytes: 42,
                      modifiedAt: '2026-05-20T10:00:00Z',
                      fileCount: null,
                    },
                ]
              : [
                  {
                    name: 'reports',
                    path: 'reports',
                    entryType: 'directory',
                    sizeBytes: null,
                    modifiedAt: '2026-05-20T10:00:00Z',
                    fileCount: 1,
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
    }));

    downloadAgentFileMock.mockResolvedValue({
      blob: new Blob(['summary']),
      fileName: 'summary.txt',
    });

    const createObjectUrl = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:download');
    const revokeObjectUrl = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);

    render(
      <AgentFilesPanel
        agent={{
          id: 'agent-1',
          name: 'Planner',
          sandboxEnabled: true,
        }}
      />
    );

    await user.click(screen.getAllByRole('button', { name: /reports/i })[1]);

    await waitFor(() => {
      expect(useAgentFilesMock).toHaveBeenLastCalledWith('agent-1', 'reports');
    });

    expect(screen.getByText((_, element) => element?.textContent === '1 files • 42 B')).toBeInTheDocument();
    expect(screen.getAllByText('summary.txt')).toHaveLength(1);

    await user.click(screen.getByRole('button', { name: 'Download summary.txt' }));

    expect(downloadAgentFileMock).toHaveBeenCalledWith('agent-1', 'reports/summary.txt');

    createObjectUrl.mockRestore();
    revokeObjectUrl.mockRestore();
  });

  it('lets chat select a workspace file reference', async () => {
    const user = userEvent.setup();
    const onSelectFile = vi.fn();
    const file = {
      name: 'summary.txt',
      path: 'reports/summary.txt',
      entryType: 'file' as const,
      sizeBytes: 42,
      modifiedAt: '2026-05-20T10:00:00Z',
      fileCount: null,
    };

    useAgentFilesMock.mockReturnValue({
      data: {
        status: 'ready',
        response: {
          agentId: 'agent-1',
          path: '',
          entries: [file],
          totalFileCount: 1,
          totalSizeBytes: 42,
        },
      },
      isPending: false,
      isFetching: false,
      error: null,
      refetch: vi.fn(),
    });

    render(
      <AgentFilesPanel
        agent={{ id: 'agent-1', name: 'Planner', sandboxEnabled: true }}
        onSelectFile={onSelectFile}
      />
    );

    await user.click(screen.getByRole('button', { name: 'Use this file: summary.txt' }));

    expect(onSelectFile).toHaveBeenCalledWith(file);
  });

  it('lists files in the selected workspace and marks a chosen path as added', async () => {
    const user = userEvent.setup();
    const file = {
      name: 'summary.txt',
      path: '\\reports\\summary.txt',
      entryType: 'file' as const,
      sizeBytes: 42,
      modifiedAt: '2026-05-20T10:00:00Z',
      fileCount: null,
    };
    useAgentFilesMock.mockReturnValue({
      data: {
        status: 'ready',
        response: {
          agentId: 'agent-1', path: '', entries: [file], totalFileCount: 1, totalSizeBytes: 42,
        },
      },
      isPending: false,
      isFetching: false,
      error: null,
      refetch: vi.fn(),
    });

    function Harness() {
      const [paths, setPaths] = React.useState<string[]>([]);
      return (
        <WorkspaceFilePickerDialog
          open
          onOpenChange={vi.fn()}
          agent={{ id: 'agent-1', name: 'Planner', sandboxEnabled: true }}
          selectedPaths={paths}
          onSelectPath={(path) => setPaths((current) => [...current, path])}
        />
      );
    }

    render(<Harness />);
    expect(screen.getByRole('heading', { name: "Choose files from Planner’s workspace" })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Use this file: summary.txt' }));
    expect(screen.getByRole('button', { name: 'Added summary.txt' })).toBeDisabled();
  });

  it('adds an uploaded workspace path from inside the picker dialog', async () => {
    const upload = vi.fn().mockResolvedValue({ path: 'reports', fileName: 'new.txt' });
    const onSelectPath = vi.fn();
    useAgentFilesMock.mockReturnValue({
      data: {
        status: 'ready',
        response: { agentId: 'agent-1', path: '', entries: [], totalFileCount: 0, totalSizeBytes: 0 },
      },
      isPending: false,
      isFetching: false,
      error: null,
      refetch: vi.fn(),
    });
    useUploadAgentFileMock.mockReturnValue({ mutateAsync: upload, isPending: false });

    render(
      <WorkspaceFilePickerDialog
        open
        onOpenChange={vi.fn()}
        agent={{ id: 'agent-1', name: 'Planner', sandboxEnabled: true }}
        selectedPaths={[]}
        onSelectPath={onSelectPath}
      />
    );
    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    fireEvent.change(input, { target: { files: [new File(['hello'], 'new.txt', { type: 'text/plain' })] } });
    await waitFor(() => expect(onSelectPath).toHaveBeenCalledWith('reports/new.txt'));
  });

  it('sends selected uploads and folder creation through the feature mutations', async () => {
    const user = userEvent.setup();
    const upload = vi.fn().mockResolvedValue({ fileName: 'summary.txt' });
    const createFolder = vi.fn().mockResolvedValue({ name: 'drafts' });

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
      isPending: false,
      isFetching: false,
      error: null,
      refetch: vi.fn(),
    });
    useUploadAgentFileMock.mockReturnValue({ mutateAsync: upload, isPending: false });
    useCreateAgentFolderMock.mockReturnValue({ mutateAsync: createFolder, isPending: false });

    render(
      <AgentFilesPanel
        agent={{
          id: 'agent-1',
          name: 'Planner',
          sandboxEnabled: true,
        }}
      />
    );

    await user.type(screen.getByPlaceholderText('New folder'), 'drafts');
    await user.click(screen.getByRole('button', { name: 'Create folder' }));

    expect(createFolder).toHaveBeenCalledWith({ name: 'drafts' });

    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    const file = new File(['hello'], 'summary.txt', { type: 'text/plain' });
    fireEvent.change(input, { target: { files: [file] } });

    await waitFor(() => {
      expect(upload).toHaveBeenCalledWith({ file });
    });
  });

  it('uploads a dropped file from the files list area', async () => {
    const upload = vi.fn().mockResolvedValue({ fileName: 'summary.txt' });

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
      isPending: false,
      isFetching: false,
      error: null,
      refetch: vi.fn(),
    });
    useUploadAgentFileMock.mockReturnValue({ mutateAsync: upload, isPending: false });

    render(
      <AgentFilesPanel
        agent={{
          id: 'agent-1',
          name: 'Planner',
          sandboxEnabled: true,
        }}
      />
    );

    const dropZone = screen.getByText('This folder is empty.').closest('div');
    const file = new File(['hello'], 'summary.txt', { type: 'text/plain' });

    fireEvent.dragEnter(dropZone!, {
      dataTransfer: {
        files: [file],
        types: ['Files'],
      },
    });

    expect(screen.getByText('Drop file to upload here')).toBeInTheDocument();

    fireEvent.drop(dropZone!, {
      dataTransfer: {
        files: [file],
        types: ['Files'],
      },
    });

    await waitFor(() => {
      expect(upload).toHaveBeenCalledWith({ file });
    });
  });

  it('deletes entries through the feature mutation', async () => {
    const user = userEvent.setup();
    const remove = vi.fn().mockResolvedValue(undefined);

    useAgentFilesMock.mockReturnValue({
      data: {
        status: 'ready',
        response: {
          agentId: 'agent-1',
          path: '',
          entries: [
            {
              name: 'summary.txt',
              path: 'summary.txt',
              entryType: 'file',
              sizeBytes: 42,
              modifiedAt: '2026-05-20T10:00:00Z',
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
    useDeleteAgentFileEntryMock.mockReturnValue({ mutateAsync: remove, isPending: false });

    render(
      <AgentFilesPanel
        agent={{
          id: 'agent-1',
          name: 'Planner',
          sandboxEnabled: true,
        }}
      />
    );

    await user.click(screen.getByRole('button', { name: 'Delete summary.txt' }));

    await waitFor(() => {
      expect(remove).toHaveBeenCalledWith({ entryPath: 'summary.txt' });
    });
    expect(globalThis.confirm).toHaveBeenCalledWith('Delete this file: "summary.txt"?');
  });

  it('retries uploads with overwrite when the server reports a name conflict', async () => {
    const user = userEvent.setup();
    const upload = vi
      .fn()
      .mockRejectedValueOnce({ response: { status: 409 }, message: 'conflict' })
      .mockResolvedValueOnce({ fileName: 'summary.txt' });
    vi.spyOn(axios, 'isAxiosError').mockReturnValue(true);

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
      isPending: false,
      isFetching: false,
      error: null,
      refetch: vi.fn(),
    });
    useUploadAgentFileMock.mockReturnValue({ mutateAsync: upload, isPending: false });

    render(
      <AgentFilesPanel
        agent={{
          id: 'agent-1',
          name: 'Planner',
          sandboxEnabled: true,
        }}
      />
    );

    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    const file = new File(['hello'], 'summary.txt', { type: 'text/plain' });
    fireEvent.change(input, { target: { files: [file] } });

    await waitFor(() => {
      expect(upload).toHaveBeenNthCalledWith(1, { file });
      expect(upload).toHaveBeenNthCalledWith(2, { file, overwrite: true });
    });

    expect(globalThis.confirm).toHaveBeenCalled();
    await user.unhover(document.body);
  });
});

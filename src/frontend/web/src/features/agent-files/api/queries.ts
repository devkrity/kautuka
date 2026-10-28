import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createAgentFolder, deleteAgentFileEntry, listAgentFiles, searchAgentFiles, uploadAgentFile } from './agentFiles';
import { agentFilesQueryKeys } from './queryKeys';

export function useAgentFiles(agentId: string | null, path: string) {
  return useQuery({
    queryKey: agentFilesQueryKeys.directory(agentId ?? 'no-agent', path),
    queryFn: () => {
      if (!agentId) {
        throw new Error('Select an agent first.');
      }

      return listAgentFiles(agentId, path);
    },
    enabled: Boolean(agentId),
    retry: false,
  });
}

export function useSearchAgentFiles(agentId: string | null, query: string, limit = 8) {
  return useQuery({
    queryKey: agentFilesQueryKeys.search(agentId ?? 'no-agent', query, limit),
    queryFn: () => {
      if (!agentId) {
        throw new Error('Select an agent first.');
      }

      return searchAgentFiles(agentId, query, limit);
    },
    enabled: Boolean(agentId) && query.trim().length > 0,
    retry: false,
  });
}

export function useUploadAgentFile(agentId: string | null, path: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ file, overwrite = false }: { file: File; overwrite?: boolean }) => {
      if (!agentId) {
        throw new Error('Select an agent first.');
      }

      return uploadAgentFile(agentId, path, file, overwrite);
    },
    onSuccess: async () => {
      if (!agentId) {
        return;
      }

      await queryClient.invalidateQueries({
        queryKey: agentFilesQueryKeys.directory(agentId, path),
        exact: true,
      });
    },
  });
}

export function useCreateAgentFolder(agentId: string | null, path: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ name }: { name: string }) => {
      if (!agentId) {
        throw new Error('Select an agent first.');
      }

      return createAgentFolder(agentId, path, name);
    },
    onSuccess: async () => {
      if (!agentId) {
        return;
      }

      await queryClient.invalidateQueries({
        queryKey: agentFilesQueryKeys.directory(agentId, path),
        exact: true,
      });
    },
  });
}

export function useDeleteAgentFileEntry(agentId: string | null, path: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ entryPath }: { entryPath: string }) => {
      if (!agentId) {
        throw new Error('Select an agent first.');
      }

      await deleteAgentFileEntry(agentId, entryPath);
    },
    onSuccess: async () => {
      if (!agentId) {
        return;
      }

      await queryClient.invalidateQueries({
        queryKey: agentFilesQueryKeys.directory(agentId, path),
        exact: true,
      });
    },
  });
}
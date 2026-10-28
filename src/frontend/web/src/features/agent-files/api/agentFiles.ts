import axios from 'axios';
import { apiClient } from '@/lib/apiClient';
import {
  agentFileCreateFolderResponseSchema,
  agentFileListResponseSchema,
  agentFileSearchResponseSchema,
  agentFilesDisabledResponseSchema,
  agentFileUploadResponseSchema,
  type AgentFileCreateFolderResponse,
  type AgentFilesQueryResult,
  type AgentFileSearchResponse,
  type AgentFileUploadResponse,
} from '../schemas/agentFileSchema';

export interface DownloadedAgentFile {
  blob: Blob;
  fileName: string;
}

const normalizeAgentFileApiPath = (path: string): string => {
  if (path.startsWith('/api/')) {
    return path.slice('/api'.length);
  }

  if (path.startsWith('http://') || path.startsWith('https://')) {
    const parsed = new URL(path);
    return parsed.pathname.startsWith('/api/')
      ? `${parsed.pathname.slice('/api'.length)}${parsed.search}`
      : `${parsed.pathname}${parsed.search}`;
  }

  return path;
};

const parseContentDispositionFileName = (value: unknown): string | null => {
  if (typeof value !== 'string' || value.trim().length === 0) {
    return null;
  }

  const utf8Match = /filename\*=\s*UTF-8''([^;]+)/i.exec(value);
  if (utf8Match?.[1]) {
    return decodeURIComponent(utf8Match[1]);
  }

  const quotedMatch = /filename="?([^";]+)"?/i.exec(value);
  return quotedMatch?.[1] ?? null;
};

export async function listAgentFiles(agentId: string, path: string): Promise<AgentFilesQueryResult> {
  try {
    const response = await apiClient.get(`/agents/${encodeURIComponent(agentId)}/files`, {
      params: path ? { path } : undefined,
    });

    return {
      status: 'ready',
      response: agentFileListResponseSchema.parse(response.data),
    };
  } catch (error) {
    if (axios.isAxiosError(error) && error.response?.status === 409) {
      return {
        status: 'disabled',
        response: agentFilesDisabledResponseSchema.parse(error.response.data),
      };
    }

    throw error;
  }
}

export async function searchAgentFiles(
  agentId: string,
  query: string,
  limit = 8
): Promise<AgentFileSearchResponse> {
  const response = await apiClient.get(`/agents/${encodeURIComponent(agentId)}/files/search`, {
    params: { query, limit },
  });

  return agentFileSearchResponseSchema.parse(response.data);
}

export async function uploadAgentFile(
  agentId: string,
  path: string,
  file: File,
  overwrite = false
): Promise<AgentFileUploadResponse> {
  const formData = new FormData();
  formData.append('file', file);
  formData.append('path', path);
  formData.append('overwrite', String(overwrite));

  const response = await apiClient.post(`/agents/${encodeURIComponent(agentId)}/files`, formData);
  return agentFileUploadResponseSchema.parse(response.data);
}

export async function createAgentFolder(
  agentId: string,
  path: string,
  name: string
): Promise<AgentFileCreateFolderResponse> {
  const response = await apiClient.post(`/agents/${encodeURIComponent(agentId)}/files/folders`, {
    path,
    name,
  });

  return agentFileCreateFolderResponseSchema.parse(response.data);
}

export async function deleteAgentFileEntry(agentId: string, path: string): Promise<void> {
  await apiClient.delete(`/agents/${encodeURIComponent(agentId)}/files`, {
    params: { path },
  });
}

export async function downloadAgentFile(
  agentId: string,
  path: string
): Promise<DownloadedAgentFile> {
  const response = await apiClient.get(
    normalizeAgentFileApiPath(`/api/agents/${encodeURIComponent(agentId)}/files/content`),
    {
      params: { path },
      responseType: 'blob',
    }
  );

  return {
    blob: response.data as Blob,
    fileName: parseContentDispositionFileName(response.headers['content-disposition'])
      ?? path.split('/').filter(Boolean).slice(-1)[0]
      ?? 'download',
  };
}

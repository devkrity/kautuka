/**
 * Chat feature API operations
 */
import { apiClient } from '@/lib/apiClient';
import { readApiArray } from '@/lib/apiResponse';
import axios from 'axios';
import {
  conversationAssetSchema,
  conversationAssetsResponseSchema,
  parsedAssetStatusSchema,
  parsedAssetStatusesSchema,
  sessionSchema,
  type ConversationAssetPayload,
  type ParsedAssetStatusPayload,
  type Session,
} from '../schemas/sessionSchema';

const readApiErrorMessage = (value: unknown): string | null => {
  if (!value || typeof value !== 'object') {
    return null;
  }

  const entries = new Map(Object.entries(value as Record<string, unknown>));
  const message = entries.get('message');
  if (typeof message === 'string' && message.trim()) {
    return message.trim();
  }

  const detail = entries.get('detail');
  if (typeof detail === 'string' && detail.trim()) {
    return detail.trim();
  }

  const error = entries.get('error');
  if (typeof error === 'string' && error.trim()) {
    return error.trim();
  }

  return null;
};

const toUploadError = (error: unknown): Error => {
  if (axios.isAxiosError(error)) {
    const apiMessage = readApiErrorMessage(error.response?.data);
    if (apiMessage) {
      return new Error(apiMessage);
    }
  }

  return error instanceof Error ? error : new Error('Upload failed');
};

const normalizeAssetApiPath = (downloadUrl: string): string => {
  if (!downloadUrl) {
    return downloadUrl;
  }

  if (downloadUrl.startsWith('/api/')) {
    return downloadUrl.slice('/api'.length);
  }

  if (downloadUrl.startsWith('http://') || downloadUrl.startsWith('https://')) {
    const parsed = new URL(downloadUrl);
    return parsed.pathname.startsWith('/api/')
      ? `${parsed.pathname.slice('/api'.length)}${parsed.search}`
      : `${parsed.pathname}${parsed.search}`;
  }

  return downloadUrl;
};

export async function createSession(
  targetType: string,
  targetId: string,
  source = 'chat'
): Promise<Session> {
  const response = await apiClient.post('/sessions', { targetType, targetId, source });
  return sessionSchema.parse(response.data);
}

export async function fetchSession(sessionId: string): Promise<Session> {
  const response = await apiClient.get(`/sessions/${sessionId}`);
  return sessionSchema.parse(response.data);
}

export async function deleteSession(sessionId: string): Promise<void> {
  await apiClient.delete(`/sessions/${sessionId}`);
}

export async function compactSession(sessionId: string): Promise<Session> {
  const response = await apiClient.post(`/sessions/${sessionId}/compact`, {});
  return sessionSchema.parse(response.data);
}

export async function listRecentSessions(limit = 20): Promise<Session[]> {
  const response = await apiClient.get('/sessions/recent', { params: { limit } });
  const sessions = readApiArray(response.data, 'sessions');
  return sessions.map((s: unknown) => sessionSchema.parse(s));
}

export async function listSessionsForAgent(agentId: string): Promise<Session[]> {
  const response = await apiClient.get(`/sessions/agents/${agentId}`);
  const sessions = readApiArray(response.data, 'sessions');
  return sessions.map((s: unknown) => sessionSchema.parse(s));
}

export async function uploadConversationAsset(
  sessionId: string,
  file: File,
  origin: 'userUpload' | 'clipboardPaste',
  disposition: 'inlinePreview' | 'download' = 'inlinePreview'
): Promise<ConversationAssetPayload> {
  const formData = new FormData();
  formData.append('file', file);
  formData.append('origin', origin);
  formData.append('disposition', disposition);

  try {
    const response = await apiClient.post(`/sessions/${sessionId}/assets`, formData);
    return conversationAssetSchema.parse(response.data);
  } catch (error) {
    throw toUploadError(error);
  }
}

export async function listConversationAssets(
  sessionId: string,
  assetIds?: readonly string[]
): Promise<ConversationAssetPayload[]> {
  const response = await apiClient.get(`/sessions/${sessionId}/assets`, {
    params: assetIds && assetIds.length > 0 ? { ids: assetIds.join(',') } : undefined,
  });
  return conversationAssetsResponseSchema.parse(response.data).assets;
}

export async function fetchConversationAssetBlob(downloadUrl: string): Promise<Blob> {
  const response = await apiClient.get(normalizeAssetApiPath(downloadUrl), {
    responseType: 'blob',
  });
  return response.data as Blob;
}

export async function fetchParsedAssetStatus(
  sessionId: string,
  assetId: string
): Promise<ParsedAssetStatusPayload | null> {
  try {
    const response = await apiClient.get(`/sessions/${sessionId}/assets/${assetId}/parsed`);
    return parsedAssetStatusSchema.parse(response.data);
  } catch (error) {
    if (axios.isAxiosError(error) && error.response?.status === 404) {
      return null;
    }

    throw error;
  }
}

export async function fetchParsedAssetStatuses(
  sessionId: string
): Promise<ParsedAssetStatusPayload[]> {
  const response = await apiClient.get(`/sessions/${sessionId}/assets/parsed`);
  const payload = parsedAssetStatusesSchema.parse(response.data);
  return payload.assets;
}

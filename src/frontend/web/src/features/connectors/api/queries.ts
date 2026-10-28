/**
 * Connectors feature TanStack Query hooks
 */
import { queryClient } from '@/lib/queryClient';
import { useMutation, useQuery } from '@tanstack/react-query';
import type { ConnectorSavePayload } from '../types/connectorDraft';
import {
    beginConnectorOAuth,
    completeConnectorOAuth,
    deleteConnector,
    disconnectConnectorOAuth,
    fetchConnector,
    fetchConnectorCredentialStatus,
    fetchConnectorOAuth,
    fetchConnectors,
    saveConnector,
  saveConnectorDiscovery,
    saveConnectorCredentials,
    testConnector,
    type ConnectorTestPayload,
} from './connectors';
import { connectorsQueryKeys } from './queryKeys';

export function useConnectors() {
  return useQuery({
    queryKey: connectorsQueryKeys.list(),
    queryFn: fetchConnectors,
  });
}

export function useConnector(connectorId: string | null) {
  return useQuery({
    queryKey: connectorId ? connectorsQueryKeys.detail(connectorId) : ['connectors', 'no-id'],
    queryFn: () =>
      connectorId ? fetchConnector(connectorId) : Promise.reject(new Error('No connector ID')),
    enabled: !!connectorId,
  });
}

export function useSaveConnector() {
  return useMutation({
    mutationFn: ({ connectorId, data }: { connectorId: string; data: ConnectorSavePayload }) =>
      saveConnector(connectorId, data),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: connectorsQueryKeys.list() });
    },
  });
}

export function useDeleteConnector() {
  return useMutation({
    mutationFn: (connectorId: string) => deleteConnector(connectorId),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: connectorsQueryKeys.list() });
    },
  });
}

export function useSaveConnectorDiscovery() {
  return useMutation({
    mutationFn: ({
      connectorId,
      toolCount,
      toolNames,
    }: {
      connectorId: string;
      toolCount: number | null;
      toolNames: string[] | null;
    }) => saveConnectorDiscovery(connectorId, { toolCount, toolNames }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: connectorsQueryKeys.list() });
    },
  });
}

export function useTestConnector() {
  return useMutation({
    mutationFn: (data: ConnectorTestPayload) => testConnector(data),
  });
}

export function useConnectorCredentialStatus(connectorId: string | null) {
  return useQuery({
    queryKey: connectorId
      ? connectorsQueryKeys.credentials(connectorId)
      : ['connectors', 'no-creds'],
    queryFn: () =>
      connectorId
        ? fetchConnectorCredentialStatus(connectorId)
        : Promise.reject(new Error('No connector ID')),
    enabled: !!connectorId,
  });
}

export function useSaveConnectorCredentials() {
  return useMutation({
    mutationFn: ({
      connectorId,
      entries,
      httpUrl,
    }: {
      connectorId: string;
      entries: Record<string, string>;
      httpUrl?: string | null;
    }) => saveConnectorCredentials(connectorId, entries, httpUrl),
    onSuccess: (_data, { connectorId }) => {
      void queryClient.invalidateQueries({
        queryKey: connectorsQueryKeys.credentials(connectorId),
      });
    },
  });
}

export function useConnectorOAuth(enabled = true) {
  return useQuery({
    queryKey: connectorsQueryKeys.oauth(),
    queryFn: fetchConnectorOAuth,
    enabled,
  });
}

export function useBeginConnectorOAuth() {
  return useMutation({
    mutationFn: beginConnectorOAuth,
  });
}

export function useCompleteConnectorOAuth() {
  return useMutation({
    mutationFn: completeConnectorOAuth,
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: connectorsQueryKeys.oauth() });
    },
  });
}

export function useDisconnectConnectorOAuth() {
  return useMutation({
    mutationFn: disconnectConnectorOAuth,
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: connectorsQueryKeys.oauth() });
    },
  });
}

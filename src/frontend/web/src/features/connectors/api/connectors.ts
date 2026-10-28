/**
 * Connectors feature API operations
 *
 * Responses use the Gateway's canonical camelCase JSON contract.
 */
import { apiClient } from '@/lib/apiClient';
import {
  readApiArray,
  readApiBoolean,
  readApiNullableString,
  readApiNumber,
  readApiRecord,
  readApiString,
  readApiStringArray,
} from '@/lib/apiResponse';
import {
  connectorMcpbImportResultSchema,
  connectorSchema,
  connectorTestResultSchema,
  type Connector,
  type ConnectorMcpbImportResult,
  type ConnectorTestResult,
} from '../schemas/connectorSchema';
import type { ConnectorSavePayload } from '../types/connectorDraft';

export interface ConnectorTestPayload {
  id?: string | null;
  transport: string;
  httpUrl: string | null;
  localCommand: string | null;
  localArgs: string[];
  containerImage: string | null;
  containerCommand: string | null;
  containerArgs: string[];
  containerSidecarName: string | null;
  bundlePath: string | null;
  serverType: string | null;
  readinessTimeoutSeconds: number | null;
  jitCooldownSeconds: number | null;
  healthcheckUrl: string | null;
  healthcheckIntervalMilliseconds: number | null;
  mcpProtocolVersion: string | null;
  envKeys: string[];
  testEnv: Record<string, string>;
}

function normalizeConnector(value: unknown): Connector {
  const normalized = {
    id: readApiString(value, 'id') ?? '',
    name: readApiString(value, 'name') ?? '',
    description: readApiString(value, 'description') ?? '',
    iconUrl: readApiNullableString(value, 'iconUrl') ?? '',
    presetId: readApiNullableString(value, 'presetId') ?? null,
    transport: readApiString(value, 'transport') ?? 'container',
    httpUrl: readApiNullableString(value, 'httpUrl') ?? null,
    containerImage: readApiNullableString(value, 'containerImage') ?? null,
    containerCommand: readApiNullableString(value, 'containerCommand') ?? null,
    containerArgs: readApiStringArray(value, 'containerArgs') ?? [],
    containerSidecarName: readApiNullableString(value, 'containerSidecarName') ?? null,
    bundlePath: readApiNullableString(value, 'bundlePath') ?? null,
    serverType: readApiNullableString(value, 'serverType') ?? null,
    readinessTimeoutSeconds: readApiNumber(value, 'readinessTimeoutSeconds') ?? null,
    jitCooldownSeconds: readApiNumber(value, 'jitCooldownSeconds') ?? null,
    healthcheckUrl: readApiNullableString(value, 'healthcheckUrl') ?? null,
    healthcheckIntervalMilliseconds:
      readApiNumber(value, 'healthcheckIntervalMilliseconds') ?? null,
    mcpProtocolVersion: readApiNullableString(value, 'mcpProtocolVersion') ?? null,
    enabled: readApiBoolean(value, 'enabled') ?? true,
    toolCount: readApiNumber(value, 'toolCount') ?? null,
    toolNames: readApiStringArray(value, 'toolNames') ?? null,
    envKeys: readApiStringArray(value, 'envKeys') ?? [],
    scope: readApiString(value, 'scope') ?? 'system',
    ownerUserId: readApiNullableString(value, 'ownerUserId') ?? null,
    createdAt: readApiString(value, 'createdAt') ?? '',
    updatedAt: readApiString(value, 'updatedAt') ?? '',
  };
  return connectorSchema.parse(normalized);
}

function normalizeTestResult(value: unknown): ConnectorTestResult {
  const toolDescriptions = readApiRecord(value, 'toolDescriptions');
  const normalized = {
    success: readApiBoolean(value, 'success') ?? false,
    message: readApiString(value, 'message') ?? 'Test request failed.',
    toolCount: readApiNumber(value, 'toolCount') ?? null,
    packageChecked: readApiNullableString(value, 'packageChecked') ?? null,
    toolNames: readApiStringArray(value, 'toolNames') ?? null,
    toolDescriptions: toolDescriptions
      ? Object.fromEntries(
          Object.entries(toolDescriptions).map(([key, desc]) => {
            if (desc == null) return [key, null];
            if (typeof desc === 'string') return [key, desc];
            if (typeof desc === 'number' || typeof desc === 'boolean') return [key, String(desc)];
            return [key, null];
          })
        )
      : null,
  };
  return connectorTestResultSchema.parse(normalized);
}

function normalizeMcpbImportResult(value: unknown): ConnectorMcpbImportResult {
  const connectorRaw =
    value && typeof value === 'object' ? (value as Record<string, unknown>).connector : null;
  const normalized = {
    outcome: readApiString(value, 'outcome') ?? 'failed',
    message: readApiString(value, 'message') ?? 'Import failed.',
    serverId: readApiString(value, 'serverId') ?? '',
    manifestVersion: readApiString(value, 'manifestVersion') ?? '',
    serverType: readApiString(value, 'serverType') ?? '',
    connector: connectorRaw ? normalizeConnector(connectorRaw) : null,
  };

  return connectorMcpbImportResultSchema.parse(normalized);
}

export async function fetchConnectors(): Promise<Connector[]> {
  const response = await apiClient.get('/connectors');
  const data = readApiArray(response.data, 'connectors');
  return data.map(normalizeConnector);
}

export async function fetchConnector(connectorId: string): Promise<Connector> {
  const response = await apiClient.get(`/connectors/${connectorId}`);
  return normalizeConnector(response.data);
}

export async function saveConnector(
  connectorId: string,
  data: ConnectorSavePayload
): Promise<Connector> {
  const response = await apiClient.post(`/connectors/${connectorId}`, data);
  return normalizeConnector(response.data);
}

export async function saveConnectorDiscovery(
  connectorId: string,
  data: { toolCount: number | null; toolNames: string[] | null }
): Promise<Connector> {
  const response = await apiClient.put(`/connectors/${connectorId}/discovery`, data);
  return normalizeConnector(response.data);
}

export async function deleteConnector(connectorId: string): Promise<void> {
  await apiClient.delete(`/connectors/${connectorId}`);
}

export async function testConnector(data: ConnectorTestPayload): Promise<ConnectorTestResult> {
  const response = await apiClient.post('/connectors/test', data);
  return normalizeTestResult(response.data);
}

export async function importMcpbConnectorBundle(file: File): Promise<ConnectorMcpbImportResult> {
  const formData = new FormData();
  formData.append('file', file, file.name);

  const response = await apiClient.post('/connectors/import/mcpb', formData);
  return normalizeMcpbImportResult(response.data);
}

export interface ConnectorCredentialStatus {
  connectorId: string;
  configuredKeys: string[];
  missingKeys: string[];
  httpUrl: string | null;
}

function normalizeCredentialStatus(value: unknown, connectorId: string): ConnectorCredentialStatus {
  const obj = value && typeof value === 'object' ? (value as Record<string, unknown>) : {};
  const configured = Array.isArray(obj.configuredKeys)
    ? (obj.configuredKeys as unknown[]).filter((k): k is string => typeof k === 'string')
    : [];
  const missing = Array.isArray(obj.missingKeys)
    ? (obj.missingKeys as unknown[]).filter((k): k is string => typeof k === 'string')
    : [];
  const httpUrl = typeof obj.httpUrl === 'string' ? obj.httpUrl : null;
  return { connectorId, configuredKeys: configured, missingKeys: missing, httpUrl };
}

export async function fetchConnectorCredentialStatus(
  connectorId: string
): Promise<ConnectorCredentialStatus> {
  const response = await apiClient.get(`/credentials/connector/${connectorId}`);
  return normalizeCredentialStatus(response.data, connectorId);
}

export interface ConnectorOAuthStatus {
  connectorId: string;
  connected: boolean;
  hasSavedCredential: boolean;
  configured: boolean;
  message: string;
  signInLabel: string;
  revokeUrl: string;
}

export interface ConnectorOAuthBegin {
  status: string;
  message: string;
  startUrl: string | null;
}

export interface ConnectorOAuthComplete {
  status: string;
  message: string;
}

function readObject(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' ? (value as Record<string, unknown>) : {};
}

function readOAuthStatus(value: unknown): ConnectorOAuthStatus | null {
  const body = readObject(value);
  if (typeof body.connectorId !== 'string') {
    return null;
  }

  return {
    connectorId: body.connectorId,
    connected: body.connected === true,
    hasSavedCredential: body.hasSavedCredential === true,
    configured: body.configured === true,
    message: typeof body.message === 'string' ? body.message : '',
    signInLabel: typeof body.signInLabel === 'string' ? body.signInLabel : 'Sign in',
    revokeUrl: typeof body.revokeUrl === 'string' ? body.revokeUrl : '',
  };
}

export async function fetchConnectorOAuth(): Promise<ConnectorOAuthStatus[]> {
  const response = await apiClient.get('/connectors/oauth');
  const body = readObject(response.data);
  return Array.isArray(body.connectors)
    ? body.connectors
        .map(readOAuthStatus)
        .filter((item): item is ConnectorOAuthStatus => item !== null)
    : [];
}

export async function beginConnectorOAuth(input: {
  connectorId: string;
  origin: string;
}): Promise<ConnectorOAuthBegin> {
  const response = await apiClient.post(
    `/connectors/${encodeURIComponent(input.connectorId)}/oauth/begin`,
    {
      origin: input.origin,
    }
  );
  const body = readObject(response.data);
  return {
    status: typeof body.status === 'string' ? body.status : 'error',
    message: typeof body.message === 'string' ? body.message : '',
    startUrl: typeof body.startUrl === 'string' ? body.startUrl : null,
  };
}

export async function completeConnectorOAuth(input: {
  code: string;
  state: string;
  brokerTicket?: string;
}): Promise<ConnectorOAuthComplete> {
  const response = await apiClient.post('/connectors/oauth/complete', input);
  const body = readObject(response.data);
  return {
    status: typeof body.status === 'string' ? body.status : 'error',
    message: typeof body.message === 'string' ? body.message : '',
  };
}

export async function disconnectConnectorOAuth(
  connectorId: string
): Promise<{ revokeUrl: string; message: string }> {
  const response = await apiClient.delete(`/connectors/${encodeURIComponent(connectorId)}/oauth`);
  const body = readObject(response.data);
  return {
    message: typeof body.message === 'string' ? body.message : '',
    revokeUrl: typeof body.revokeUrl === 'string' ? body.revokeUrl : '',
  };
}

export async function saveConnectorCredentials(
  connectorId: string,
  entries: Record<string, string>,
  httpUrl?: string | null
): Promise<void> {
  await apiClient.put(`/credentials/connector/${connectorId}`, {
    entries,
    httpUrl: httpUrl && httpUrl.trim().length > 0 ? httpUrl.trim() : null,
  });
}

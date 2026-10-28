import type { Connector } from '../schemas/connectorSchema';

export type ConnectorDraft = {
  id: string;
  name: string;
  description: string;
  iconUrl: string;
  presetId: string | null;
  transport: string;
  httpUrl: string;
  localCommand: string;
  localArgs: string; // raw textarea — one per line
  containerImage: string;
  containerCommand: string;
  containerArgs: string;
  containerSidecarName: string;
  bundlePath: string;
  serverType: string;
  readinessTimeoutSeconds: string;
  jitCooldownSeconds: string;
  healthcheckUrl: string;
  healthcheckIntervalMilliseconds: string;
  mcpProtocolVersion: string;
  envKeys: string; // raw textarea — one per line
  enabled: boolean;
  toolCount: number | null;
  toolNames: string[] | null;
};

export type ConnectorSavePayload = {
  name: string;
  description: string;
  iconUrl: string;
  presetId: string | null;
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
  enabled: boolean;
  toolCount: number | null;
  toolNames: string[] | null;
};

export function blankDraft(): ConnectorDraft {
  return {
    id: '',
    name: '',
    description: '',
    iconUrl: '',
    transport: 'local',
    presetId: null,
    httpUrl: '',
    localCommand: 'npx',
    localArgs: '-y\n',
    envKeys: '',
    containerImage: '',
    containerCommand: '',
    containerArgs: '',
    containerSidecarName: '',
    bundlePath: '',
    serverType: '',
    readinessTimeoutSeconds: '',
    jitCooldownSeconds: '',
    healthcheckUrl: '',
    healthcheckIntervalMilliseconds: '',
    mcpProtocolVersion: '',
    enabled: true,
    toolCount: null,
    toolNames: null,
  };
}

export function entryToDraft(c: Connector): ConnectorDraft {
  return {
    ...c,
    presetId: c.presetId ?? null,
    httpUrl: c.httpUrl ?? '',
    localCommand: c.containerCommand ?? '',
    localArgs: c.containerArgs.join('\n'),
    containerImage: c.containerImage ?? '',
    containerCommand: c.containerCommand ?? '',
    containerArgs: c.containerArgs.join('\n'),
    containerSidecarName: c.containerSidecarName ?? '',
    bundlePath: c.bundlePath ?? '',
    serverType: c.serverType ?? '',
    readinessTimeoutSeconds:
      c.readinessTimeoutSeconds == null ? '' : String(c.readinessTimeoutSeconds),
    jitCooldownSeconds: c.jitCooldownSeconds == null ? '' : String(c.jitCooldownSeconds),
    healthcheckUrl: c.healthcheckUrl ?? '',
    healthcheckIntervalMilliseconds:
      c.healthcheckIntervalMilliseconds == null ? '' : String(c.healthcheckIntervalMilliseconds),
    mcpProtocolVersion: c.mcpProtocolVersion ?? '',
    envKeys: (c.envKeys ?? []).join('\n'),
    iconUrl: c.iconUrl ?? '',
    toolCount: c.toolCount ?? null,
    toolNames: c.toolNames ?? null,
  };
}

export function buildSavePayload(d: ConnectorDraft): ConnectorSavePayload {
  return {
    name: d.name,
    description: d.description,
    iconUrl: d.iconUrl,
    presetId: d.presetId,
    transport: d.transport,
    httpUrl: d.httpUrl || null,
    localCommand: d.localCommand || null,
    localArgs: d.localArgs
      .split('\n')
      .map((s) => s.trim())
      .filter(Boolean),
    containerImage: d.containerImage || null,
    containerCommand: d.containerCommand || null,
    containerArgs: d.containerArgs
      .split('\n')
      .map((s) => s.trim())
      .filter(Boolean),
    containerSidecarName: d.containerSidecarName || null,
    bundlePath: d.bundlePath || null,
    serverType: d.serverType || null,
    readinessTimeoutSeconds: Number.parseInt(d.readinessTimeoutSeconds, 10) || null,
    jitCooldownSeconds: Number.parseInt(d.jitCooldownSeconds, 10) || null,
    healthcheckUrl: d.healthcheckUrl || null,
    healthcheckIntervalMilliseconds: Number.parseInt(d.healthcheckIntervalMilliseconds, 10) || null,
    mcpProtocolVersion: d.mcpProtocolVersion || null,
    envKeys: d.envKeys
      .split('\n')
      .map((s) => s.trim())
      .filter(Boolean),
    enabled: d.enabled,
    toolCount: d.toolCount ?? null,
    toolNames: d.toolNames ?? null,
  };
}

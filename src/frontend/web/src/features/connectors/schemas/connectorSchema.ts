/**
 * Connectors (MCP servers) feature Zod schemas
 *
 * Uses camelCase to mirror the normalized shape produced by the API layer.
 */
import { z } from 'zod';

export const ownershipScopeSchema = z.enum(['system', 'user']);

export const connectorSchema = z.object({
  id: z.string(),
  name: z.string(),
  description: z.string().default(''),
  iconUrl: z.string().default(''),
  presetId: z.string().nullable().default(null),
  transport: z.string().default('container'),
  httpUrl: z.string().nullable().default(null),
  containerImage: z.string().nullable().default(null),
  containerCommand: z.string().nullable().default(null),
  containerArgs: z.array(z.string()).default([]),
  containerSidecarName: z.string().nullable().default(null),
  bundlePath: z.string().nullable().default(null),
  serverType: z.string().nullable().default(null),
  readinessTimeoutSeconds: z.number().nullable().default(null),
  jitCooldownSeconds: z.number().nullable().default(null),
  healthcheckUrl: z.string().nullable().default(null),
  healthcheckIntervalMilliseconds: z.number().nullable().default(null),
  mcpProtocolVersion: z.string().nullable().default(null),
  enabled: z.boolean().default(true),
  isInternalSystemConnector: z.boolean().default(false),
  toolCount: z.number().nullable().default(null),
  toolNames: z.array(z.string()).nullable().default(null),
  envKeys: z.array(z.string()).default([]),
  scope: ownershipScopeSchema.default('system'),
  ownerUserId: z.string().nullable().default(null),
  createdAt: z.string().default(''),
  updatedAt: z.string().default(''),
});

export type Connector = z.infer<typeof connectorSchema>;

export const connectorTestResultSchema = z.object({
  success: z.boolean().default(false),
  message: z.string().default(''),
  toolCount: z.number().nullable().optional().default(null),
  packageChecked: z.string().nullable().optional().default(null),
  toolNames: z.array(z.string()).nullable().optional().default(null),
  toolDescriptions: z.record(z.string(), z.string().nullable()).nullable().optional().default(null),
});

export type ConnectorTestResult = z.infer<typeof connectorTestResultSchema>;

export const connectorMcpbImportResultSchema = z.object({
  outcome: z.enum(['created', 'updated', 'failed']).default('failed'),
  message: z.string().default(''),
  serverId: z.string().default(''),
  manifestVersion: z.string().default(''),
  serverType: z.string().default(''),
  connector: connectorSchema.nullable().default(null),
});

export type ConnectorMcpbImportResult = z.infer<typeof connectorMcpbImportResultSchema>;

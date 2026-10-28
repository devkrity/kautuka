/**
 * Connectors feature query key factory
 */

export const connectorsQueryKeys = {
  all: ['connectors'] as const,
  list: () => [...connectorsQueryKeys.all, 'list'] as const,
  detail: (connectorId: string) => [...connectorsQueryKeys.all, connectorId] as const,
  credentials: (connectorId: string) =>
    [...connectorsQueryKeys.all, connectorId, 'credentials'] as const,
  oauth: () => [...connectorsQueryKeys.all, 'oauth'] as const,
};

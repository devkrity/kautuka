import { fetchConnectors, testConnector } from './connectors';
import type { Connector } from '../schemas/connectorSchema';
import { toast } from 'sonner';
import { useEffect } from 'react';
import { queryClient } from '@/lib/queryClient';
import { connectorsQueryKeys } from './queryKeys';

const builtInIds = new Set(['kautuka-web-tools', 'kautuka-google-mail', 'kautuka-m365-mail']);

export function useBuiltInToolRefresh(enabled: boolean, isAdmin: boolean): void {
  useEffect(() => {
    if (!enabled) return undefined;
    let active = true;
    let busy = false;
    const attempted = new Map<string, number>();
    const refresh = async () => {
      if (busy) return;
      busy = true;
      try {
        const changed = await refreshBuiltInTools(attempted, () => active, isAdmin);
        if (active && changed) await queryClient.invalidateQueries({ queryKey: connectorsQueryKeys.list() });
      } catch { /* Retry when Gateway is available. */ }
      finally { busy = false; }
    };
    void refresh();
    const timer = window.setInterval(() => void refresh(), 30000);
    return () => { active = false; window.clearInterval(timer); };
  }, [enabled, isAdmin]);
}

/** Refresh only enabled shipped connectors whose saved tool definitions are missing. */
export async function refreshBuiltInTools(attempted: Map<string, number>, active: () => boolean, isAdmin: boolean): Promise<boolean> {
  const connectors = await fetchConnectors();
  let refreshed = false;
  for (const connector of connectors) {
    if (!active()) return refreshed;
    if (!builtInIds.has(connector.id) || connector.scope !== 'system' || !connector.enabled || connector.toolCount !== null) continue;
    if (!isAdmin && (connector.id !== 'kautuka-web-tools' || connector.transport !== 'container'
      || !connector.bundlePath || connector.envKeys?.length)) continue;
    const key = `${connector.id}:${connector.updatedAt}`;
    const attempts = attempted.get(key) ?? 0;
    if (attempts >= 2) continue;
    attempted.set(key, attempts + 1);
    try {
      const result = await refreshConnector(connector);
      refreshed ||= result.success;
      if (result.success) attempted.set(key, 2);
      if (!result.success && attempts === 1 && active()) toast.warning(`${connector.name} needs attention`, { description: 'Open Connectors and use Test connection to retry.' });
    } catch {
      if (attempts === 1 && active()) toast.warning(`${connector.name} needs attention`, { description: 'Open Connectors and use Test connection to retry.' });
    }
  }
  return refreshed;
}

async function refreshConnector(connector: Connector) {
  return testConnector({
    id: connector.id, transport: connector.transport, httpUrl: connector.httpUrl,
    localCommand: null, localArgs: [], containerImage: connector.containerImage,
    containerCommand: connector.containerCommand, containerArgs: connector.containerArgs,
    containerSidecarName: connector.containerSidecarName, bundlePath: connector.bundlePath,
    serverType: connector.serverType, readinessTimeoutSeconds: connector.readinessTimeoutSeconds,
    jitCooldownSeconds: connector.jitCooldownSeconds, healthcheckUrl: connector.healthcheckUrl,
    healthcheckIntervalMilliseconds: connector.healthcheckIntervalMilliseconds,
    mcpProtocolVersion: connector.mcpProtocolVersion, envKeys: connector.envKeys, testEnv: {},
  });
}

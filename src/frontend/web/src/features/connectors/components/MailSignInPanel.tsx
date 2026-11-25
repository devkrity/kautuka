import { Button } from '@/components/ui/button';
import { useEffect, useRef, useState } from 'react';
import { toast } from 'sonner';
import { runBoundMailLogin } from '@/features/auth';
import { apiClient } from '@/lib/apiClient';
import type { ConnectorOAuthStatus } from '../api/connectors';
import {
  useBeginConnectorOAuth,
  useConnectorOAuth,
  useDisconnectConnectorOAuth,
} from '../api/queries';

export function matchesConnectorOAuth(connectorId: string, status: ConnectorOAuthStatus): boolean {
  return connectorId === status.connectorId || connectorId.endsWith(`::${status.connectorId}`);
}

export function ConnectorSignInPanel({ connectorId }: { connectorId: string }) {
  const catalog = useConnectorOAuth();
  const begin = useBeginConnectorOAuth();
  const disconnect = useDisconnectConnectorOAuth();
  const [note, setNote] = useState<string | null>(null);
  const [connecting, setConnecting] = useState(false);
  const [networkPrompt, setNetworkPrompt] = useState<{ destination: string; resolve: (allow: boolean) => void } | null>(null);
  const attempt = useRef<AbortController | null>(null);
  const pendingPrompt = useRef<((allow: boolean) => void) | null>(null);
  useEffect(() => {
    setConnecting(false);
    setNote(null);
    return () => { attempt.current?.abort(); attempt.current = null; pendingPrompt.current?.(false); pendingPrompt.current = null; };
  }, [connectorId]);
  const status = catalog.data?.find((item) => matchesConnectorOAuth(connectorId, item));
  if (!status) {
    return null;
  }

  const connected = status.connected;
  const checkNetwork = async (phase: 'auth' | 'mail', signal: AbortSignal) => {
    const { data } = await apiClient.post<{ allowed: boolean; requestId?: string; destination: string }>(
      `/connectors/${encodeURIComponent(connectorId)}/oauth/network-check/${phase}`,
      {}, { signal }
    );
    if (data.allowed) return;
    if (!data.requestId) throw new Error(`Access to ${data.destination} is blocked. Check Network Access.`);
    const allow = await new Promise<boolean>((resolve) => {
      const finish = (value: boolean) => { signal.removeEventListener('abort', cancel); resolve(value); };
      const cancel = () => finish(false);
      pendingPrompt.current = finish;
      signal.addEventListener('abort', cancel, { once: true });
      if (signal.aborted) { cancel(); return; }
      setNetworkPrompt({ destination: data.destination, resolve: finish });
    });
    pendingPrompt.current = null;
    setNetworkPrompt(null);
    if (!allow) throw new Error(`Access to ${data.destination} was not allowed.`);
    signal.throwIfAborted();
    const requests = await apiClient.get<{ revision: number }>('/network-access/requests', { signal });
    try {
      await apiClient.post(`/network-access/requests/${encodeURIComponent(data.requestId)}/decision`,
        { allow: true, scope: 'connector', expectedRevision: requests.data.revision }, { signal });
    } catch {
      throw new Error(`Could not allow ${data.destination}. An administrator may need to approve it in Network Access.`);
    }
    const verified = await apiClient.post<{ allowed: boolean }>(
      `/connectors/${encodeURIComponent(connectorId)}/oauth/network-check/${phase}`, {}, { signal }
    );
    if (!verified.data.allowed) throw new Error(`Access to ${data.destination} is still blocked. Check Network Access.`);
  };
  const providerPermissionsUrl =
    disconnect.data?.revokeUrl || (!connected && status.hasSavedCredential ? status.revokeUrl : '');
  return (
    <div className="space-y-2 border-t border-border pt-2">
      <div className="flex items-center justify-between gap-2">
        <p className="text-xs text-muted-foreground">{note ?? status.message}</p>
        {connected || status.hasSavedCredential ? (
          <Button
            type="button"
            variant="outline"
            size="xs"
            className="shrink-0"
            onClick={() => {
              attempt.current?.abort();
              attempt.current = null;
              setConnecting(false);
              disconnect.mutate(connectorId, {
                onSuccess: (result) => {
                  setNote(result.message);
                },
              });
            }}
            disabled={disconnect.isPending}
          >
            {connected ? 'Disconnect' : 'Remove saved sign-in'}
          </Button>
        ) : null}
        {!connected ? (
          <Button
            type="button"
            size="xs"
            className="shrink-0"
            onClick={() => {
              if (status.boundFlow) {
                attempt.current?.abort();
                const controller = new AbortController();
                attempt.current = controller;
                setConnecting(true);
                void runBoundMailLogin(connectorId, controller.signal, () => checkNetwork('auth', controller.signal)).then(async () => {
                  if (controller.signal.aborted) return;
                  setNote('Signed in. Checking mailbox network access…');
                  try {
                    await checkNetwork('mail', controller.signal);
                    if (!controller.signal.aborted) setNote(null);
                  } catch (error) {
                    if (!controller.signal.aborted) setNote(`Signed in. ${error instanceof Error ? error.message : 'Network access could not be checked.'}`);
                  } finally {
                    if (!controller.signal.aborted) await catalog.refetch();
                  }
                }).catch((error: unknown) => {
                  if (controller.signal.aborted) return;
                  const message = error instanceof Error ? error.message : 'Mail sign-in failed.';
                  setNote(message);
                  toast.error(message);
                }).finally(() => { if (!controller.signal.aborted) setConnecting(false); });
                return;
              }
              begin.mutate(
                { connectorId, origin: window.location.origin },
                {
                  onSuccess: (result) => {
                    if (result.startUrl) {
                      window.location.assign(result.startUrl);
                      return;
                    }
                    setNote(result.message);
                    toast.error(result.message);
                  },
                }
              );
            }}
            disabled={begin.isPending || connecting || status.pending || !status.configured}
          >
            {status.signInLabel}
          </Button>
        ) : null}
      </div>
      {networkPrompt ? (
        <div role="dialog" aria-label="Allow network access" className="rounded border border-border p-3 text-sm">
          <p>Allow this connector to reach {networkPrompt.destination}?</p>
          <div className="mt-2 flex gap-2">
            <Button type="button" size="xs" onClick={() => networkPrompt.resolve(true)}>Allow</Button>
            <Button type="button" size="xs" variant="outline" onClick={() => networkPrompt.resolve(false)}>Not now</Button>
          </div>
        </div>
      ) : null}
      {providerPermissionsUrl ? (
        <a
          className="inline-block text-xs text-primary underline"
          href={providerPermissionsUrl}
          target="_blank"
          rel="noreferrer"
        >
          Manage provider permissions
        </a>
      ) : null}
    </div>
  );
}

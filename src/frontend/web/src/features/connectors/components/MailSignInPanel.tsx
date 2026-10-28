import { Button } from '@/components/ui/button';
import { useState } from 'react';
import { toast } from 'sonner';
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
  const status = catalog.data?.find((item) => matchesConnectorOAuth(connectorId, item));
  if (!status) {
    return null;
  }

  const connected = status.connected;
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
            disabled={begin.isPending || !status.configured}
          >
            {status.signInLabel}
          </Button>
        ) : null}
      </div>
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

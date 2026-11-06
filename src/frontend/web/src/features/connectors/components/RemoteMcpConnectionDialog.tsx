import { useEffect, useRef, useState } from 'react';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogTitle } from '@/components/ui/dialog';
import {
  cancelRemoteMcpDraft,
  createRemoteMcpDraft,
  fetchConnectorCredentialStatus,
  saveRemoteMcpDraft,
  testRemoteMcpDraft,
  type ConnectorCredentialStatus,
} from '../api/connectors';
import type { Connector } from '../schemas/connectorSchema';
import { buildSavePayload, entryToDraft, type ConnectorDraft } from '../types/connectorDraft';
import { RemoteMcpEditor, type RemoteCredentialMutation } from './RemoteMcpEditor';

type Props = {
  connector?: Connector;
  initialDraft?: ConnectorDraft;
  initialCredentials?: Record<string, string>;
  existingIds?: Set<string>;
  canManage?: boolean;
  canManageSystemCredentials?: boolean;
  onSaved: (connector: Connector) => void;
  onClose: () => void;
};

/** Only the credential-admin creation request contains the owner's newly entered values. */
export function RemoteMcpConnectionDialog({
  connector,
  initialDraft,
  initialCredentials,
  existingIds,
  canManage = true,
  canManageSystemCredentials = false,
  onSaved,
  onClose,
}: Props) {
  const [status, setStatus] = useState<ConnectorCredentialStatus>();
  const [loading, setLoading] = useState(!!connector);
  const [statusFailed, setStatusFailed] = useState(false);
  const handle = useRef<string | null>(null);
  const identity = useRef<string | null>(connector?.id ?? null);
  const revision = useRef(0);
  const alive = useRef(true);

  const discard = () => {
    revision.current++;
    const previous = handle.current;
    handle.current = null;
    if (!connector) identity.current = null;
    if (previous)
      void cancelRemoteMcpDraft(previous).catch(() => {
        /* Server-side expiry guarantees cleanup after a failed cancel. */
      });
  };

  useEffect(() => {
    alive.current = true;
    let stopped = false;
    if (connector) {
      void fetchConnectorCredentialStatus(connector.id)
        .then((value) => {
          if (!stopped) setStatus(value);
        })
        .catch(() => {
          if (!stopped) setStatusFailed(true);
        })
        .finally(() => {
          if (!stopped) setLoading(false);
        });
    }
    return () => {
      stopped = true;
      alive.current = false;
      const previous = handle.current;
      handle.current = null;
      if (previous)
        void cancelRemoteMcpDraft(previous).catch(() => {
          /* Expiry is server-authoritative. */
        });
    };
  }, [connector]);

  const ensureDraft = async (
    draft: ConnectorDraft,
    changes: RemoteCredentialMutation[],
    scope: 'user' | 'system'
  ) => {
    if (handle.current) return handle.current;
    if (!identity.current)
      identity.current = existingIds?.has(draft.id)
        ? `${draft.id}-${crypto.randomUUID().slice(0, 8)}`
        : draft.id;
    const version = revision.current;
    const created = await createRemoteMcpDraft({
      connectorId: identity.current,
      candidate: buildSavePayload(draft),
      credentialScope: scope,
      entries: Object.fromEntries(
        changes.flatMap((change) =>
          change.action === 'replace' ? [[change.key, change.value]] : []
        )
      ),
      removeKeys: changes.flatMap((change) => (change.action === 'remove' ? [change.key] : [])),
    });
    if (!alive.current || version !== revision.current) {
      await cancelRemoteMcpDraft(created.draftId).catch(() => {
        /* Expiry handles an unavailable server. */
      });
      throw new Error('The connector draft was canceled.');
    }
    handle.current = created.draftId;
    return created.draftId;
  };

  const discardExpired = (failure: unknown) => {
    if (failure && typeof failure === 'object' && 'response' in failure) {
      const response = failure.response;
      if (
        response &&
        typeof response === 'object' &&
        'status' in response &&
        (response.status === 404 || response.status === 410)
      )
        discard();
    }
  };

  if (loading)
    return (
      <Dialog
        open
        onOpenChange={(open) => {
          if (!open) onClose();
        }}
      >
        <DialogContent onPointerDownOutside={(event) => event.preventDefault()}>
          <DialogTitle>Configure remote MCP</DialogTitle>
          <p>Checking saved credential status…</p>
          <Button type="button" variant="outline" onClick={onClose}>
            Cancel
          </Button>
        </DialogContent>
      </Dialog>
    );
  const candidate =
    initialDraft ??
    (connector
      ? {
          ...entryToDraft(connector),
          ...(!canManage && status?.httpUrl ? { httpUrl: status.httpUrl } : {}),
        }
      : undefined);
  const personalDestination = !!status?.httpUrl && status.httpUrl !== connector?.httpUrl;
  return (
    <>
      <RemoteMcpEditor
        connector={connector}
        initialDraft={candidate}
        initialCredentials={initialCredentials}
        configuredKeys={status?.configuredKeys}
        credentialPresence={status?.credentials}
        canManage={canManage}
        canManageSystemCredentials={canManageSystemCredentials && !personalDestination}
        credentialStatusWarning={
          statusFailed
            ? 'Saved credential status could not be checked. Blank fields preserve saved values.'
            : undefined
        }
        onDraftChanged={discard}
        onTest={async (draft, changes, scope = 'user') => {
          try {
            return await testRemoteMcpDraft(await ensureDraft(draft, changes, scope));
          } catch (failure) {
            discardExpired(failure);
            throw failure;
          }
        }}
        onSave={async (draft, changes, scope = 'user') => {
          try {
            const saved = await saveRemoteMcpDraft(await ensureDraft(draft, changes, scope));
            handle.current = null;
            onSaved(saved);
          } catch (failure) {
            discardExpired(failure);
            throw failure;
          }
        }}
        onClose={() => {
          discard();
          onClose();
        }}
      />
    </>
  );
}

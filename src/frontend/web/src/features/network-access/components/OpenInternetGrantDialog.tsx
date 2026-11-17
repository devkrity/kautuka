import React from 'react';
import { useQuery } from '@tanstack/react-query';
import { useConnectors } from '@/features/connectors';
import { settingsAdmin } from '@/features/settings';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';

interface Props {
  onClose: () => void;
  onConfirm: (userId: string, connectorId: string) => Promise<void>;
}

export function OpenInternetGrantDialog({ onClose, onConfirm }: Props) {
  const [userId, setUserId] = React.useState('');
  const [connectorId, setConnectorId] = React.useState('');
  const [saving, setSaving] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const users = useQuery({ queryKey: ['network-access', 'users'], queryFn: async () => (await settingsAdmin.listUsers()).data.users });
  const connectors = useConnectors();
  const available = (connectors.data ?? []).filter(connector => connector.transport === 'container'
    && !connector.isInternalSystemConnector && (connector.scope === 'system' || connector.ownerUserId === userId));

  const confirm = async () => {
    if (!userId || !available.some(connector => connector.id === connectorId)) return;
    setSaving(true);
    setError(null);
    try {
      await onConfirm(userId, connectorId);
      onClose();
    } catch {
      setError('Could not confirm this rule was saved. Refresh the rules before retrying; this grant requires an isolated connector.');
    } finally {
      setSaving(false);
    }
  };

  return <Dialog open onOpenChange={(open) => { if (!open && !saving) onClose(); }}>
    <DialogContent>
      <DialogHeader>
        <DialogTitle>Allow a connector to reach public websites</DialogTitle>
        <DialogDescription>This lets the selected user's connector send data to any public internet address, on any port. Only allow connectors you trust. This rule does not grant access to the local network or cloud metadata; other rules and blocks still apply.</DialogDescription>
      </DialogHeader>
      {users.isError || connectors.isError ? <p role="alert">Could not load users or connectors. Close this dialog and try again.</p> : <div className="space-y-3">
        <label className="block">User
          <select aria-label="User" className="block w-full rounded border p-2" value={userId} onChange={event => { setUserId(event.target.value); setConnectorId(''); }}>
            <option value="">Select a user</option>
            {(users.data ?? []).map(user => <option key={user.id} value={user.id}>{user.displayName || user.username}</option>)}
          </select>
        </label>
        <label className="block">Connector
          <select aria-label="Connector" className="block w-full rounded border p-2" value={connectorId} onChange={event => setConnectorId(event.target.value)} disabled={!userId}>
            <option value="">Select a connector</option>
            {available.map(connector => <option key={connector.id} value={connector.id}>{connector.name}</option>)}
          </select>
        </label>
        <p className="text-xs text-muted-foreground">Only connectors available to you are shown. Manager checks whether the selected connector can enforce this rule.</p>
      </div>}
      {error && <p role="alert">{error}</p>}
      <DialogFooter>
        <Button type="button" variant="outline" onClick={onClose} disabled={saving}>Cancel</Button>
        <Button type="button" variant="destructive" disabled={saving || !userId || !connectorId || users.isError || connectors.isError} onClick={() => void confirm()}>Allow any public website</Button>
      </DialogFooter>
    </DialogContent>
  </Dialog>;
}

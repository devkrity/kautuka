import React from 'react';
import { Button } from '@/components/ui/button';
import { useAgents } from '@/features/settings';
import { useConnectors } from '@/features/connectors';
import type { CreateOrUpdateRuleDto, NetworkAccessRuleDto } from '../networkAccess';

interface RuleScopeEditorProps {
  rule: NetworkAccessRuleDto;
  userId: string;
  isAdmin: boolean;
  expectedRevision: number;
  onCancel: () => void;
  onSave: (rule: CreateOrUpdateRuleDto) => Promise<void>;
}

type Target = 'system' | 'user' | 'agent' | 'connector';

export function RuleScopeEditor(props: RuleScopeEditorProps) {
  return <RuleScopeEditorForm key={props.rule.id} {...props} />;
}

function RuleScopeEditorForm({ rule, userId, isAdmin, expectedRevision, onCancel, onSave }: RuleScopeEditorProps) {
  const ownerId = rule.userId || userId;
  const initialTarget: Target = rule.scopeKind === 'system' ? 'system' : rule.scopeKind === 'agent' ? 'agent' : rule.connectorId ? 'connector' : 'user';
  const [target, setTarget] = React.useState<Target>(initialTarget);
  const [agentId, setAgentId] = React.useState(rule.agentId ?? '');
  const [connectorId, setConnectorId] = React.useState(rule.connectorId ?? '');
  const [allowed, setAllowed] = React.useState(rule.allowed);
  const [saving, setSaving] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const agents = useAgents();
  const connectors = useConnectors();
  const availableAgents = (agents.data ?? []).filter(agent => agent.scope === 'system' || agent.ownerUserId === ownerId);
  const availableConnectors = (connectors.data ?? []).filter(connector => connector.scope === 'system' || connector.ownerUserId === ownerId);

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      await onSave({
        scopeKind: target === 'connector' ? 'user' : target,
        userId: target === 'system' ? undefined : ownerId,
        agentId: target === 'agent' ? agentId : undefined,
        connectorId: target === 'connector' ? connectorId : undefined,
        destination: rule.destination, destKind: rule.destKind, port: rule.port,
        allowed, allowLan: allowed && rule.allowLan, expectedRevision, replaceRuleId: rule.id,
      });
    } catch {
      setError('Could not save this rule. Check the selected scope and try again.');
    } finally {
      setSaving(false);
    }
  };

  const invalidTarget = (target === 'agent' && !agentId) || (target === 'connector' && !connectorId) || (target !== 'system' && !ownerId);
  return (
    <div role="dialog" aria-modal="true" aria-labelledby="edit-network-rule-title" className="rounded-xl border border-border bg-card p-4 space-y-3">
      <h3 id="edit-network-rule-title" className="font-semibold">Edit network rule for {rule.destination}</h3>
      <label className="block">Applies to
        <select aria-label="Applies to" value={target} onChange={event => setTarget(event.target.value as Target)} className="block w-full rounded border p-2">
          {isAdmin && <option value="system" disabled={rule.destKind === 'any-public'}>Entire platform</option>}
          <option value="user" disabled={rule.destKind === 'any-public'}>User</option>
          <option value="agent" disabled={rule.destKind === 'any-public'}>User and agent</option>
          <option value="connector">User and connector</option>
        </select>
      </label>
      {target === 'agent' && <label className="block">Agent
        <select aria-label="Agent" value={agentId} onChange={event => setAgentId(event.target.value)} className="block w-full rounded border p-2">
          <option value="">Select an agent</option>
          {rule.agentId && !availableAgents.some(agent => agent.id === rule.agentId) && <option value={rule.agentId}>{rule.agentDisplayName || rule.agentId}</option>}
          {availableAgents.map(agent => <option key={agent.id} value={agent.id}>{agent.name}</option>)}
        </select>
      </label>}
      {target === 'connector' && <label className="block">Connector
        <select aria-label="Connector" value={connectorId} onChange={event => setConnectorId(event.target.value)} className="block w-full rounded border p-2">
          <option value="">Select a connector</option>
          {rule.connectorId && !availableConnectors.some(connector => connector.id === rule.connectorId) && <option value={rule.connectorId}>{rule.connectorDisplayName || rule.connectorId}</option>}
          {availableConnectors.map(connector => <option key={connector.id} value={connector.id}>{connector.name}</option>)}
        </select>
      </label>}
      <label className="block">Decision
        <select aria-label="Decision" value={allowed ? 'allow' : 'block'} onChange={event => setAllowed(event.target.value === 'allow')} className="block w-full rounded border p-2">
          <option value="allow">Allow</option><option value="block">Block</option>
        </select>
      </label>
      {error && <p role="alert">{error}</p>}
      <div className="flex gap-2"><Button type="button" disabled={saving || invalidTarget} onClick={() => void save()}>Save rule</Button>
        <Button type="button" variant="outline" disabled={saving} onClick={onCancel}>Cancel</Button></div>
    </div>
  );
}

import { useEffect, useState } from 'react';
import { Button } from '@/components/ui/button';
import { apiClient } from '@/lib/apiClient';
import type { NetworkAccessRuleDto, NetworkRulesResponseDto } from '@/features/network-access';

const groups = [
  { id: 'sign-in', name: 'social sign-in', label: 'Social sign-in' },
  { id: 'telegram', name: 'Telegram', label: 'Telegram' },
  { id: 'slack', name: 'Slack', label: 'Slack' },
  { id: 'updates', name: 'in-app updates', label: 'In-app updates' },
] as const;
type ServicePermission = (typeof groups)[number];

function serviceTargets(rules: NetworkAccessRuleDto[], service: ServicePermission) {
  return rules.filter(rule => rule.setupService === service.id && rule.scopeKind === 'agent'
    && rule.userId === 'system' && rule.agentId && rule.destKind === 'host');
}

function serviceState(rules: NetworkAccessRuleDto[], targets: NetworkAccessRuleDto[]) {
  const grants = targets.filter(rule => rule.allowed && !rule.paused);
  const allowed = grants.filter(target => !rules.some(rule => rule.scopeKind === 'system'
    && rule.destination === target.destination && rule.port === target.port && !rule.allowed && !rule.paused));
  return { granted: grants.length, allowed: allowed.length };
}

export function ChannelSettingsSection({ onReviewReady }: { onReviewReady?: (ready: boolean) => void }) {
  const [rules, setRules] = useState<NetworkAccessRuleDto[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const load = async () => {
    const response = await apiClient.get<NetworkRulesResponseDto>('/network-access/rules');
    setRules(response.data.rules);
    return response.data;
  };
  useEffect(() => { void load().catch(() => setMessage('Could not load service network settings.')); }, []);
  useEffect(() => {
    onReviewReady?.(rules !== null && !busy && groups.some(group => serviceTargets(rules, group).length > 0));
  }, [onReviewReady, rules, busy]);

  async function setService(service: ServicePermission, targets: NetworkAccessRuleDto[], allow: boolean) {
    setBusy(true); setMessage('');
    try {
      for (const target of targets) {
        const current = await load();
        await apiClient.put('/network-access/rules', {
          scopeKind: 'agent', userId: 'system', agentId: target.agentId, destination: target.destination,
          destKind: 'host', port: target.port, allowed: allow, allowLan: false, expectedRevision: current.revision,
        });
      }
      const updated = await load();
      const result = serviceState(updated.rules, serviceTargets(updated.rules, service));
      const applied = allow ? result.allowed === targets.length : result.granted === 0;
      if (service.id === 'updates' || service.id === 'sign-in') {
        setMessage(applied
          ? `${service.label} ${allow ? 'enabled' : 'paused'}.`
          : `Check the network rules for ${service.name}.`);
        return;
      }
      setMessage(allow
        ? applied ? 'Channel enabled.' : 'Another network rule is preventing this channel from being enabled.'
        : applied ? 'Channel paused. Saved account settings are kept.' : 'The channel is only partly paused. Check the setting and try again.');
    } catch {
      await load().catch(() => setRules(null));
      setMessage('Could not finish updating the permission. Check the current setting and try again.');
    } finally { setBusy(false); }
  }

  return <section aria-label="Service network access" className="space-y-3 rounded-xl border border-border/70 p-4">
    <div><h3 className="font-semibold">Service network access</h3>
      <p className="text-sm text-muted-foreground">Enable the services you want to use. Pausing a chat channel keeps saved account settings.</p></div>
    {rules && groups.map(service => {
      const targets = serviceTargets(rules, service);
      if (targets.length === 0) return null;
      const state = serviceState(rules, targets);
      const status = state.granted > state.allowed ? 'Blocked by another network rule'
        : state.allowed === targets.length ? 'Allowed by this setting' : state.allowed > 0 ? 'Partially allowed' : 'Paused';
      return <div key={service.name} className="flex flex-wrap items-center justify-between gap-3">
        <div><span>{service.label}: {status}</span>
          <p className="text-xs text-muted-foreground break-all">{targets.map(rule => rule.destination).join(', ')}{service.id === 'slack' ? ' and Slack subdomains' : ''}</p></div>
        <div className="flex gap-2">
          {state.granted < targets.length && <Button type="button" variant="outline" disabled={busy} onClick={() => void setService(service, targets, true)}>Enable {service.name}</Button>}
          {state.granted > 0 && <Button type="button" variant="outline" disabled={busy} onClick={() => void setService(service, targets, false)}>Pause {service.name}</Button>}
        </div>
      </div>;
    })}
    {message && <p role="status" className="text-sm">{message}</p>}
    {rules === null && message && <Button type="button" variant="outline" onClick={() => {
      setMessage('');
      void load().catch(() => setMessage('Could not load service network settings.'));
    }}>Retry loading permissions</Button>}
  </section>;
}

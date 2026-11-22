import { useEffect, useState } from 'react';
import { Button } from '@/components/ui/button';
import { apiClient } from '@/lib/apiClient';
import type { NetworkAccessRuleDto, NetworkRulesResponseDto } from '@/features/network-access';

const channels = [
  { name: 'Telegram', agentId: 'channelhost', hosts: ['api.telegram.org'] },
  { name: 'Slack', agentId: 'channelhost', hosts: ['slack.com'] },
] as const;
const updates = {
  name: 'in-app updates', agentId: 'updateservice',
  hosts: ['api.github.com', 'github.com', 'release-assets.githubusercontent.com'],
} as const;
type ServicePermission = (typeof channels)[number] | typeof updates;

function serviceState(rules: NetworkAccessRuleDto[], service: ServicePermission) {
  const grants = service.hosts.filter(host => rules.some(rule => rule.scopeKind === 'agent' && rule.userId === 'system'
    && rule.agentId === service.agentId && rule.destination === host && rule.port === 443 && rule.allowed && !rule.paused));
  const allowed = grants.filter(host => !rules.some(rule => rule.scopeKind === 'system'
    && rule.destination === host && rule.port === 443 && !rule.allowed && !rule.paused));
  return { granted: grants.length, allowed: allowed.length };
}

export function ChannelSettingsSection() {
  const [rules, setRules] = useState<NetworkAccessRuleDto[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const load = async () => {
    const response = await apiClient.get<NetworkRulesResponseDto>('/network-access/rules');
    setRules(response.data.rules);
    return response.data;
  };
  useEffect(() => { void load().catch(() => setMessage('Could not load service network settings.')); }, []);

  async function setService(service: ServicePermission, allow: boolean) {
    setBusy(true); setMessage('');
    try {
      for (const host of service.hosts) {
        const current = await load();
        await apiClient.put('/network-access/rules', {
          scopeKind: 'agent', userId: 'system', agentId: service.agentId, destination: host,
          destKind: 'host', port: 443, allowed: allow, allowLan: false, expectedRevision: current.revision,
        });
      }
      const updated = await load();
      const result = serviceState(updated.rules, service);
      const applied = allow ? result.allowed === service.hosts.length : result.granted === 0;
      if (service.agentId === 'updateservice') {
        setMessage(applied
          ? `In-app updates ${allow ? 'enabled' : 'paused'}.`
          : 'Check the network rules for in-app updates.');
        return;
      }
      setMessage(allow
        ? applied ? 'Channel enabled.' : 'Another network rule is preventing this channel from being enabled.'
        : applied ? 'Channel paused. Saved account settings are kept.' : 'The channel is only partly paused. Check the setting and try again.');
    } catch {
      await load().catch(() => undefined);
      setMessage('Could not finish updating the permission. Check the current setting and try again.');
    } finally { setBusy(false); }
  }

  const services: readonly ServicePermission[] = rules?.some(rule => rule.agentId === 'updateservice') ? [...channels, updates] : channels;
  return <section aria-label="Service network access" className="space-y-3 rounded-xl border border-border/70 p-4">
    <div><h3 className="font-semibold">Service network access</h3>
      <p className="text-sm text-muted-foreground">Enable the services you want to use. Pausing a chat channel keeps saved account settings.</p></div>
    {rules && services.map(service => {
      const state = serviceState(rules, service);
      const label = service.agentId === 'updateservice' ? 'In-app updates' : service.name;
      const status = state.granted > state.allowed ? 'Blocked by another network rule'
        : state.allowed === service.hosts.length ? 'Enabled' : state.allowed > 0 ? 'Partially enabled' : 'Paused';
      return <div key={service.name} className="flex flex-wrap items-center justify-between gap-3">
        <div><span>{label}: {status}</span>
          <p className="text-xs text-muted-foreground break-all">{service.hosts.join(', ')}{service.name === 'Slack' ? ' and Slack subdomains' : ''}</p></div>
        <div className="flex gap-2">
          {state.granted < service.hosts.length && <Button type="button" variant="outline" disabled={busy} onClick={() => void setService(service, true)}>Enable {service.name}</Button>}
          {state.granted > 0 && <Button type="button" variant="outline" disabled={busy} onClick={() => void setService(service, false)}>Pause {service.name}</Button>}
        </div>
      </div>;
    })}
    {message && <p role="status" className="text-sm">{message}</p>}
  </section>;
}

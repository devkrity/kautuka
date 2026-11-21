import { useEffect, useState } from 'react';
import { Button } from '@/components/ui/button';
import { apiClient } from '@/lib/apiClient';
import type { NetworkAccessRuleDto, NetworkRulesResponseDto } from '@/features/network-access';

const channels = [
  { name: 'Telegram', hosts: ['api.telegram.org'] },
  { name: 'Slack', hosts: ['slack.com'] },
] as const;

function channelState(rules: NetworkAccessRuleDto[], hosts: readonly string[]): 'enabled' | 'paused' | 'partial' {
  const allowed = hosts.map(host => rules.some(rule => rule.scopeKind === 'agent' && rule.userId === 'system'
    && rule.agentId === 'channelhost' && rule.destination === host && rule.port === 443 && rule.allowed && !rule.paused)
    && !rules.some(rule => rule.scopeKind === 'system' && rule.destination === host && rule.port === 443 && !rule.allowed));
  return allowed.every(Boolean) ? 'enabled' : allowed.some(Boolean) ? 'partial' : 'paused';
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
  useEffect(() => { void load().catch(() => setMessage('Could not load channel settings.')); }, []);

  async function setChannel(hosts: readonly string[], allow: boolean) {
    setBusy(true); setMessage('');
    try {
      for (const host of hosts) {
        const current = await load();
        await apiClient.put('/network-access/rules', {
          scopeKind: 'agent', userId: 'system', agentId: 'channelhost', destination: host,
          destKind: 'host', port: 443, allowed: allow, allowLan: false, expectedRevision: current.revision,
        });
      }
      const updated = await load();
      const result = channelState(updated.rules, hosts);
      setMessage(allow
        ? result === 'enabled' ? 'Channel enabled.' : 'Another network rule is preventing this channel from being enabled.'
        : result === 'paused' ? 'Channel paused. Saved account settings are kept.' : 'The channel is only partly paused. Check the setting and try again.');
    } catch {
      await load().catch(() => undefined);
      setMessage('Could not finish updating the channel. Some changes may have applied; check the setting and try again.');
    } finally { setBusy(false); }
  }

  return <section aria-label="Chat channels" className="space-y-3 rounded-xl border border-border/70 p-4">
    <div><h3 className="font-semibold">Chat channels</h3>
      <p className="text-sm text-muted-foreground">Allow people to connect their own accounts. Pausing a channel keeps their saved settings.</p></div>
    {rules && channels.map(channel => {
      const state = channelState(rules, channel.hosts);
      return <div key={channel.name} className="flex items-center justify-between gap-3">
        <span>{channel.name}: {state === 'partial' ? 'Partially enabled' : state === 'enabled' ? 'Enabled' : 'Paused'}</span>
        <div className="flex gap-2">
          {state !== 'enabled' && <Button type="button" variant="outline" disabled={busy} onClick={() => void setChannel(channel.hosts, true)}>Enable {channel.name}</Button>}
          {state !== 'paused' && <Button type="button" variant="outline" disabled={busy} onClick={() => void setChannel(channel.hosts, false)}>Pause {channel.name}</Button>}
        </div>
      </div>;
    })}
    {message && <p role="status" className="text-sm">{message}</p>}
  </section>;
}

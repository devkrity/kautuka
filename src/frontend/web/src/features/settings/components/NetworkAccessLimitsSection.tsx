import { useEffect, useState } from 'react';
import { apiClient } from '@/lib/apiClient';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';

type Limits = { global: number; perUser: number };

export function NetworkAccessLimitsSection() {
  const [limits, setLimits] = useState<Limits | null>(null);
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let active = true;
    void apiClient.get<Limits>('/settings/system/network-access').then(response => {
      if (active) setLimits(response.data);
    }).catch(() => { if (active) setMessage('Could not load network access settings.'); });
    return () => { active = false; };
  }, []);

  async function save() {
    if (!limits || !Number.isInteger(limits.global) || !Number.isInteger(limits.perUser)
      || limits.global < 1 || limits.perUser < 1 || limits.perUser > limits.global) {
      setMessage('Enter positive whole numbers. The per-user limit cannot exceed the total limit.');
      return;
    }
    setBusy(true); setMessage('');
    try {
      const response = await apiClient.put<Limits>('/settings/system/network-access', limits);
      setLimits(response.data); setMessage('Network access settings saved.');
    } catch { setMessage('Could not confirm the settings were saved. Reload to check.'); }
    finally { setBusy(false); }
  }

  return <section aria-label="Network access" className="space-y-3 rounded-xl border border-border/70 p-4">
    <div><h3 className="font-semibold">Network access requests</h3>
      <p className="text-sm text-muted-foreground">Limit the number of requests waiting for a decision. Existing requests are kept if a limit is lowered.</p></div>
    {limits && <>
      <label className="block text-sm">Total pending requests<Input type="number" min="1" value={limits.global} onChange={e => setLimits({ ...limits, global: Number(e.target.value) })} /></label>
      <label className="block text-sm">Pending requests per user<Input type="number" min="1" value={limits.perUser} onChange={e => setLimits({ ...limits, perUser: Number(e.target.value) })} /></label>
      <Button type="button" disabled={busy} onClick={() => void save()}>Save network access settings</Button>
    </>}
    {message && <p role="status" className="text-sm">{message}</p>}
  </section>;
}

import { useEffect, useState } from 'react';
import { apiClient } from '@/lib/apiClient';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';

type Broker = { origin: string; googleSocialClientId: string | null; microsoftSocialClientId: string | null };
type BrokerSettings = { active: Broker; pending: Broker | null; switchPending: boolean };

export function AuthenticationBrokerSection() {
  const [settings, setSettings] = useState<BrokerSettings | null>(null);
  const [origin, setOrigin] = useState('');
  const [google, setGoogle] = useState('');
  const [microsoft, setMicrosoft] = useState('');
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);
  const [outcomeUnknown, setOutcomeUnknown] = useState(false);

  function show(value: BrokerSettings) {
    setSettings(value);
    const selected = value.pending ?? value.active;
    setOrigin(selected.origin);
    setGoogle(selected.googleSocialClientId ?? '');
    setMicrosoft(selected.microsoftSocialClientId ?? '');
  }
  useEffect(() => {
    let live = true;
    void apiClient.get<BrokerSettings>('/settings/system/authentication-broker').then(response => {
      if (live) show(response.data);
    }).catch(() => { if (live) setMessage('Could not load authentication broker settings.'); });
    return () => { live = false; };
  }, []);

  async function save(value: Broker) {
    setBusy(true); setMessage(''); setOutcomeUnknown(false);
    try {
      const response = await apiClient.put<BrokerSettings>('/settings/system/authentication-broker', value);
      show(response.data);
      setMessage(response.data.pending ? 'Saved. Restart Kautuka to apply these settings.' : 'Pending change canceled.');
    } catch (error: unknown) {
      const result = error as { response?: { status?: number; data?: { detail?: string } } };
      const uncertain = !result.response || (result.response.status ?? 500) >= 500;
      const failure = result.response?.data?.detail ?? (uncertain ? 'Save result is uncertain.' : 'Could not save authentication broker settings.');
      setMessage(failure);
      try {
        const current = (await apiClient.get<BrokerSettings>('/settings/system/authentication-broker')).data;
        show(current);
        const matchesBundle = (a: Broker, b: Broker) => a.origin === b.origin
          && a.googleSocialClientId === b.googleSocialClientId && a.microsoftSocialClientId === b.microsoftSocialClientId;
        if (current.pending) {
          const matches = matchesBundle(current.pending, value);
          setMessage(`${matches ? '' : `${failure} `}Settings for ${current.pending.origin} are pending. Restart Kautuka to apply them.`);
        } else if (!current.switchPending && matchesBundle(current.active, value)) setMessage('Pending change canceled.');
        else if (uncertain) setMessage('Save result is uncertain. Check the settings before trying again.');
      } catch {
        if (uncertain) {
          setOutcomeUnknown(true);
          setMessage('Save result is unknown. Reload settings to check before trying again.');
        }
      }
    } finally { setBusy(false); }
  }

  if (!settings) return <section aria-label="Authentication broker"><p>{message || 'Loading authentication broker settings…'}</p></section>;
  return <section aria-label="Authentication broker" className="space-y-3 rounded-xl border border-border/70 p-4">
    <div><h3 className="font-semibold">Authentication broker</h3>
      <p className="text-sm text-muted-foreground">Use Kautuka’s broker or your own HTTPS service. Changes apply after a restart.</p></div>
    <label className="block text-sm">Broker origin<Input value={origin} onChange={event => {
      const baseline = settings.pending ?? settings.active;
      if (origin === baseline.origin && event.target.value !== baseline.origin) { setGoogle(''); setMicrosoft(''); }
      setOrigin(event.target.value);
    }} placeholder="https://auth.example.com" /></label>
    <label className="block text-sm">Google sign-in client ID<Input value={google} onChange={event => setGoogle(event.target.value)} /></label>
    <label className="block text-sm">Microsoft sign-in client ID<Input value={microsoft} onChange={event => setMicrosoft(event.target.value)} /></label>
    <p className="text-xs text-muted-foreground">These public IDs are for signing in to Kautuka. Mail app credentials stay on the broker. Leave an ID blank to disable that sign-in option.</p>
    <p className="text-xs text-muted-foreground">Remove existing mail connections before changing broker origin, then reconnect them after restarting.</p>
    <p className="text-xs text-muted-foreground">Keep a local administrator password so you can sign in if the broker is unavailable.</p>
    <div className="flex gap-2"><Button type="button" disabled={busy || outcomeUnknown} onClick={() => void save({ origin: origin.trim(), googleSocialClientId: google.trim() || null, microsoftSocialClientId: microsoft.trim() || null })}>Save broker settings</Button>
      {settings.switchPending && <Button type="button" variant="outline" disabled={busy || outcomeUnknown} onClick={() => void save(settings.active)}>Cancel pending change</Button>}</div>
    {outcomeUnknown && <Button type="button" variant="outline" disabled={busy} onClick={() => {
      setBusy(true);
      void apiClient.get<BrokerSettings>('/settings/system/authentication-broker').then(response => {
        show(response.data); setOutcomeUnknown(false); setMessage('');
      }).catch(() => setMessage('Could not reload settings. Try again.')).finally(() => setBusy(false));
    }}>Reload settings</Button>}
    {settings.switchPending && !settings.pending && <p className="text-sm">The save result is uncertain. Cancel the pending change or restart Kautuka.</p>}
    {settings.pending && !message && <p role="status" className="text-sm">Settings for {settings.pending.origin} are pending. Restart Kautuka to apply them.</p>}
    {message && <p role="status" className="text-sm">{message}</p>}
  </section>;
}

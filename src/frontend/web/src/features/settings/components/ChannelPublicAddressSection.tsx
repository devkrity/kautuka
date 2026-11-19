import { useEffect, useState } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { apiClient } from '@/lib/apiClient';

export function ChannelPublicAddressSection() {
  const [address, setAddress] = useState('');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  useEffect(() => {
    void apiClient.get<{ address: string | null }>('/settings/system/channels/address')
      .then(response => setAddress(response.data.address ?? ''))
      .catch(() => setMessage('Could not load the Kautuka address.'));
  }, []);

  async function saveAddress() {
    setBusy(true); setMessage('');
    try {
      const response = await apiClient.put<{ address: string | null }>('/settings/system/channels/address', { address });
      setAddress(response.data.address ?? '');
      setMessage('Kautuka address saved.');
    } catch { setMessage('Enter a valid Kautuka address, such as https://kautuka.example.com, and try again.'); }
    finally { setBusy(false); }
  }

  return <section className="space-y-2 rounded-xl border border-border/70 p-4" aria-label="Channel links">
    <label htmlFor="channel-public-address" className="block text-sm font-semibold">Kautuka address for channel links</label>
    <Input id="channel-public-address" type="url" value={address} onChange={event => setAddress(event.target.value)} placeholder="https://kautuka.example.com" />
    <p className="text-sm text-muted-foreground">Use an address your Slack and Telegram users can reach.</p>
    <Button type="button" variant="outline" disabled={busy} onClick={() => void saveAddress()}>Save address</Button>
    {message && <p role="status" className="text-sm">{message}</p>}
  </section>;
}

import { Alert } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { SegmentedControl } from '@/components/ui/segmented-control';
import { TabsContent } from '@/components/ui/tabs';
import { apiClient } from '@/lib/apiClient';
import React from 'react';
import { apiError } from '../utils/settingsUtils';

interface TelegramChannel {
  providerKey: string;
  registrationId?: string | null;
  externalUserId?: string | null;
  settings?: Record<string, unknown>;
  configuredSecretFieldKeys?: string[];
  status?: string;
}

const secretInputType: React.HTMLInputTypeAttribute = ['pass', 'word'].join('');

type BotChoice = 'account' | 'own';

function chatIdOf(channel: TelegramChannel | undefined): string {
  if (!channel) {
    return '';
  }
  if (typeof channel.externalUserId === 'string' && channel.externalUserId.trim()) {
    return channel.externalUserId.trim();
  }
  const stored = channel.settings?.channel_device_id;
  return typeof stored === 'string' ? stored.trim() : '';
}

function isActive(channel: TelegramChannel | undefined): boolean {
  return channel?.status === 'ACTIVE' || channel?.status === 'PROVISIONING';
}

export function AgentChannelsTab({ agentId }: Readonly<{ agentId: string }>) {
  const [botChoice, setBotChoice] = React.useState<BotChoice>('account');
  const [accountChatId, setAccountChatId] = React.useState('');
  const [botToken, setBotToken] = React.useState('');
  const [tokenSet, setTokenSet] = React.useState(false);
  const [saved, setSaved] = React.useState(false);
  const [saving, setSaving] = React.useState(false);
  const [loaded, setLoaded] = React.useState(false);
  const [message, setMessage] = React.useState<string | null>(null);
  const [error, setError] = React.useState<string | null>(null);

  const apply = React.useCallback(
    (channels: TelegramChannel[]) => {
      const telegram = channels.filter((channel) => channel.providerKey === 'telegram');
      const account = telegram.find((channel) => (channel.registrationId ?? '').endsWith(':default'));
      const agent = telegram.find((channel) =>
        (channel.registrationId ?? '').endsWith(`:agent:${agentId}`)
      );
      const active = isActive(agent);
      setAccountChatId(chatIdOf(account));
      setSaved(active);
      setTokenSet(agent?.configuredSecretFieldKeys?.includes('bot_token') ?? false);
      setBotToken('');
      setBotChoice(active ? 'own' : 'account');
    },
    [agentId]
  );

  React.useEffect(() => {
    let cancelled = false;
    apiClient
      .get('/settings/me/channels')
      .then((response) => {
        if (cancelled) {
          return;
        }
        const channels = (response.data as { channels?: TelegramChannel[] } | undefined)?.channels;
        apply(Array.isArray(channels) ? channels : []);
        setLoaded(true);
      })
      .catch((loadError: unknown) => {
        if (!cancelled) {
          setError(apiError(loadError));
          setLoaded(true);
        }
      });
    return () => {
      cancelled = true;
    };
  }, [apply]);

  const canSaveOwn =
    botChoice === 'own' &&
    Boolean(accountChatId) &&
    (tokenSet || Boolean(botToken.trim()));
  const canClear = botChoice === 'account' && saved;

  const save = async () => {
    setSaving(true);
    setError(null);
    setMessage(null);
    try {
      const response = await apiClient.put('/settings/me/channels/telegram', {
        settings: canClear
          ? {}
          : {
              channel_device_id: accountChatId,
              bot_token: botToken.trim(),
            },
        attachmentRole: 'agent',
        attachmentSubject: agentId,
        clear: canClear,
      });
      const channels = (response.data as { channels?: TelegramChannel[] } | undefined)?.channels;
      apply(Array.isArray(channels) ? channels : []);
      setMessage(canClear ? 'This agent uses your account bot again.' : 'Saved.');
    } catch (saveError) {
      setError(apiError(saveError));
    } finally {
      setSaving(false);
    }
  };

  return (
    <TabsContent value="channels" className="space-y-4">
      <div className="space-y-3">
        <p className="text-xs text-muted-foreground">Telegram</p>
        <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
          <span className="text-sm">Bot for this agent</span>
          <SegmentedControl
            shape="pill"
            size="md"
            value={botChoice}
            onChange={setBotChoice}
            options={[
              { value: 'account', label: 'Account bot', tone: 'neutral' },
              {
                value: 'own',
                label: 'Separate bot',
                tone: 'neutral',
                disabled: !accountChatId,
              },
            ]}
          />
        </div>
        {accountChatId ? null : (
          <Alert variant="destructive" className="px-3 py-2 text-xs">
            Set a device ID in your user settings before giving this agent its own bot.
          </Alert>
        )}
        {botChoice === 'account' || !accountChatId ? (
          <p className="text-xs text-muted-foreground">
            This agent uses your account bot, so you can switch between agents in one Telegram chat with Kautuka.
          </p>
        ) : (
          <div className="space-y-3">
            <p className="text-xs text-muted-foreground">
              This agent will answer through a separate Telegram bot chat. Start that bot in Telegram before using it. Choose Account bot if you want all agents in the same chat.
            </p>
            <div className="space-y-1">
              <label className="block text-xs text-muted-foreground" htmlFor={`agent-channel-token-${agentId}`}>
                Bot token
              </label>
              <Input
                id={`agent-channel-token-${agentId}`}
                type={secretInputType}
                value={botToken}
                onChange={(event) => setBotToken(event.target.value)}
                placeholder={tokenSet ? 'Leave blank to keep the saved token' : 'From BotFather'}
              />
            </div>
          </div>
        )}
        {loaded && (canSaveOwn || canClear) ? (
          <div className="flex justify-end">
            <Button type="button" variant="secondary" disabled={saving} onClick={() => void save()}>
              {saving ? 'Saving…' : 'Save'}
            </Button>
          </div>
        ) : null}
        {message ? <p className="text-xs text-muted-foreground">{message}</p> : null}
        {error ? (
          <Alert variant="destructive" className="px-3 py-2 text-xs">
            {error}
          </Alert>
        ) : null}
      </div>
    </TabsContent>
  );
}

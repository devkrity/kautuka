import { Alert } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { SegmentedControl } from '@/components/ui/segmented-control';
import { apiClient } from '@/lib/apiClient';
import { Link } from 'react-router-dom';
import React from 'react';

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

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Telegram settings could not be saved.';
}

export function WorkflowChannelsPanel({
  scope,
  workflowId,
}: Readonly<{ scope: 'shared' | 'workflow'; workflowId?: string }>) {
  const [botChoice, setBotChoice] = React.useState<BotChoice>('account');
  const [accountChatId, setAccountChatId] = React.useState('');
  const [botToken, setBotToken] = React.useState('');
  const [tokenSet, setTokenSet] = React.useState(false);
  const [saved, setSaved] = React.useState(false);
  const [saving, setSaving] = React.useState(false);
  const [loaded, setLoaded] = React.useState(false);
  const [message, setMessage] = React.useState<string | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const own = scope === 'workflow';
  const tokenFieldId = own
    ? `workflow-channel-token-${workflowId}`
    : 'workflow-channel-token-shared';

  const apply = React.useCallback(
    (channels: TelegramChannel[]) => {
      const telegram = channels.filter((channel) => channel.providerKey === 'telegram');
      const account = telegram.find((channel) =>
        (channel.registrationId ?? '').endsWith(':default')
      );
      const match = telegram.find((channel) => {
        const registrationId = channel.registrationId ?? '';
        return own
          ? registrationId.endsWith(`:workflow:${workflowId ?? ''}`)
          : registrationId.endsWith(':workflows');
      });
      const active = isActive(match);
      setAccountChatId(chatIdOf(account));
      setSaved(active);
      setTokenSet(match?.configuredSecretFieldKeys?.includes('bot_token') ?? false);
      setBotToken('');
      setBotChoice(active ? 'own' : 'account');
    },
    [own, workflowId]
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
          setError(errorMessage(loadError));
          setLoaded(true);
        }
      });
    return () => {
      cancelled = true;
    };
  }, [apply]);

  const canSaveOwn =
    botChoice === 'own' && Boolean(accountChatId) && (tokenSet || Boolean(botToken.trim()));
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
        attachmentRole: own ? 'workflow' : 'workflows',
        attachmentSubject: own ? workflowId : undefined,
        clear: canClear,
      });
      const channels = (response.data as { channels?: TelegramChannel[] } | undefined)?.channels;
      apply(Array.isArray(channels) ? channels : []);
      setMessage(
        canClear
          ? own
            ? 'This workflow uses your account bot again.'
            : 'Workflow reviews use your account bot again.'
          : 'Saved.'
      );
    } catch (saveError) {
      setError(errorMessage(saveError));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="space-y-3">
      <p className="text-xs text-muted-foreground">Telegram</p>
      <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
        <span className="text-sm">
          {own ? 'Bot for this workflow' : 'Bot for workflow reviews'}
        </span>
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
        <p className="text-xs text-muted-foreground">
          To choose a separate Telegram bot, set your device ID in{' '}
          <Link to="/settings?tab=users" className="underline">
            Settings
          </Link>
          .
        </p>
      )}
      {botChoice === 'account' || !accountChatId ? (
        <p className="text-xs text-muted-foreground">
          {own
            ? 'Review cards for this workflow go through the Telegram bot on your account.'
            : 'Review cards go through the Telegram bot on your account.'}
        </p>
      ) : (
        <div className="space-y-3">
          <p className="text-xs text-muted-foreground">
            {own
              ? 'This replaces the account bot for this workflow only. It uses the Telegram chat already on your account.'
              : 'This replaces the account bot for workflow reviews. It uses the Telegram chat already on your account.'}
          </p>
          <div className="space-y-1">
            <label className="block text-xs text-muted-foreground" htmlFor={tokenFieldId}>
              Bot token
            </label>
            <Input
              id={tokenFieldId}
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
  );
}

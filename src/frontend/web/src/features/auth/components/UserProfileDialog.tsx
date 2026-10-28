import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { Banner, settingsQueryKeys, useAgents } from '@/features/settings';
import { queryClient } from '@/lib/queryClient';
import { CheckCircle2, PauseCircle, RotateCcw, Settings2, Trash2, UserRound } from 'lucide-react';
import React from 'react';
import {
  type ChannelProviderDefinition,
  type UserChannelSettings,
  changeMyPassword,
  clearMyExternalLogin,
  getMyChannelSettings,
  getMySlackAppCreationUrl,
  getMySlackPairingStatus,
  saveMyChannelSettings,
  sendMyChannelHitlTest,
  sendMySlackTestMessage,
  startMySlackPairing,
  transitionMyChannelLifecycle,
  updateMyProfile,
} from '../api/auth';
import { authQueryKeys, useCurrentUser } from '../api/queries';
import { ChannelSetupGuidePanel } from './ChannelSetupGuidePanel';
import { ExternalSignInMethods } from './ExternalSignInMethods';
import {
  getChannelSetupGuide,
  getChannelSetupOutcome,
} from './channelSetupGuides';
import { defaultDisplayName, validateProfileDraft } from './profileValidation';
import { validateSlackAppToken, validateSlackBotToken } from './slackTokenValidation';

type ResultState = { success: boolean; message: string } | null;
type ChannelDraft = {
  settings: Record<string, string>;
  channelDefaultTarget: string;
  hitlTetherMode: 'hitl' | 'mirror';
};
interface UserProfileDialogProps {
  collapsed: boolean;
  userName: string;
  userRole: string;
  userInitials: string;
  triggerClassName?: string;
}

const emptyPasswordState = {
  currentPassword: '',
  newPassword: '',
  confirmPassword: '',
};

function getOwnValue<T>(record: Readonly<Record<string, T>>, key: string): T | undefined {
  return Object.prototype.hasOwnProperty.call(record, key)
    ? (Reflect.get(record, key) as T | undefined)
    : undefined;
}

function getOwnString(record: Readonly<Record<string, unknown>>, key: string): string | undefined {
  const value = getOwnValue<unknown>(record, key);
  return typeof value === 'string' ? value : undefined;
}

function withRecordValue<T>(
  record: Readonly<Record<string, T>>,
  key: string,
  value: T
): Record<string, T> {
  const updated = { ...record };
  Reflect.set(updated, key, value);
  return updated;
}

const concealedTextInputType = 'password';

export function UserProfileDialog({
  collapsed,
  userName,
  userRole,
  userInitials,
  triggerClassName,
}: Readonly<UserProfileDialogProps>) {
  const { data: user } = useCurrentUser();
  const { data: agents = [] } = useAgents();
  const [open, setOpen] = React.useState(false);
  const [loadingChannel, setLoadingChannel] = React.useState(false);
  const [savingProfile, setSavingProfile] = React.useState(false);
  const [savingPassword, setSavingPassword] = React.useState(false);
  const [passwordFormOpen, setPasswordFormOpen] = React.useState(false);
  const [savingChannelKey, setSavingChannelKey] = React.useState<string | null>(null);
  const [result, setResult] = React.useState<ResultState>(null);
  const [providers, setProviders] = React.useState<ChannelProviderDefinition[]>([]);
  const [selectedChannelTab, setSelectedChannelTab] = React.useState('');
  const [channelSettings, setChannelSettings] = React.useState<Record<string, UserChannelSettings>>(
    {}
  );
  const [profileDraft, setProfileDraft] = React.useState({
    displayName: '',
    email: '',
  });
  const [channelDrafts, setChannelDrafts] = React.useState<Record<string, ChannelDraft>>({});
  const [passwordDraft, setPasswordDraft] = React.useState(emptyPasswordState);
  const [slackAppIdOverride, setSlackAppIdOverride] = React.useState('');
  const [slackPairingCode, setSlackPairingCode] = React.useState<string | null>(null);
  const [slackPaired, setSlackPaired] = React.useState(false);
  const [slackPairingFailureReason, setSlackPairingFailureReason] = React.useState<string | null>(null);
  const [slackPairingStatusError, setSlackPairingStatusError] = React.useState(false);

  React.useEffect(() => {
    if (!open || !user) {
      return;
    }

    setProfileDraft({
      displayName: defaultDisplayName(user.displayName, user.username),
      email: user.email ?? '',
    });
    setPasswordDraft(emptyPasswordState);
    setPasswordFormOpen(false);
    setResult(null);
    setLoadingChannel(true);
    // Channel default-target options come from the agents catalogue. Refresh on open so
    // agents created after the initial shell mount (acceptance fixtures, setup wizards)
    // appear instead of a stale empty list within staleTime.
    void queryClient.invalidateQueries({ queryKey: settingsQueryKeys.agents() });

    getMyChannelSettings()
      .then((response) => {
        setProviders(response.providers);
        setChannelSettings(
          Object.fromEntries(
            response.providers.flatMap((provider) => {
              const channel = preferChatChannel(response.channels, provider.providerKey);
              return channel ? [[provider.providerKey, channel] as const] : [];
            })
          )
        );
        setChannelDrafts(
          Object.fromEntries(
            response.providers.map((provider) => {
              const channel = preferChatChannel(response.channels, provider.providerKey);
              const defaultTarget =
                channel?.defaultTargetType && channel.defaultTargetId
                  ? `${channel.defaultTargetType}:${channel.defaultTargetId}`
                  : '';

              const settings: Record<string, string> = Object.fromEntries(
                provider.fields.map((field) => [
                  field.fieldKey,
                  getOwnString(channel?.settings ?? {}, field.fieldKey) ?? '',
                ])
              );

              return [
                provider.providerKey,
                {
                  settings,
                  channelDefaultTarget: defaultTarget,
                  hitlTetherMode: channel?.hitlTetherMode ?? 'mirror',
                } satisfies ChannelDraft,
              ];
            })
          )
        );
        const slack = preferChatChannel(response.channels, 'slack');
        if (slack?.status === 'ACTIVE') {
          void getMySlackPairingStatus()
            .then((status) => {
              setSlackPaired(status.paired);
              if (status.reason) {
                setSlackPairingFailureReason(status.reason);
                setSlackPairingCode(null);
              }
            })
            .catch(() => setSlackPaired(false));
        } else {
          setSlackPaired(false);
        }
      })
      .catch((error) => {
        setResult({
          success: false,
          message: error instanceof Error ? error.message : 'Failed to load channel settings.',
        });
      })
      .finally(() => setLoadingChannel(false));
  }, [open, user]);

  React.useEffect(() => {
    if (!open || !slackPairingCode) return;
    const timer = globalThis.setInterval(() => {
      void getMySlackPairingStatus()
        .then((status) => {
          setSlackPairingStatusError(false);
          if (status.paired) {
            setSlackPaired(true);
            setSlackPairingCode(null);
            setResult({ success: true, message: 'Slack is linked. Send a test message to check it.' });
            void getMyChannelSettings().then((response) => {
              const slack = preferChatChannel(response.channels, 'slack');
              if (slack) setChannelSettings((previous) => withRecordValue(previous, 'slack', slack));
            });
          } else if (status.reason) {
            setSlackPairingFailureReason(status.reason);
            setSlackPairingCode(null);
          }
        })
        .catch(() => setSlackPairingStatusError(true));
    }, 2500);
    return () => globalThis.clearInterval(timer);
  }, [open, slackPairingCode]);

  const saveProfile = async () => {
    const errors = validateProfileDraft(profileDraft.displayName, profileDraft.email);
    if (errors.displayName || errors.email) {
      setResult({
        success: false,
        message: errors.displayName ?? errors.email ?? 'Check your profile details.',
      });
      return;
    }
    setSavingProfile(true);
    setResult(null);
    try {
      const updated = await updateMyProfile({
        displayName: profileDraft.displayName.trim() || null,
        email: profileDraft.email.trim() || null,
      });
      queryClient.setQueryData(authQueryKeys.profile(), updated);
      setResult({ success: true, message: 'Profile updated.' });
    } catch (error) {
      setResult({
        success: false,
        message: error instanceof Error ? error.message : 'Failed to update profile.',
      });
    } finally {
      setSavingProfile(false);
    }
  };

  const savePassword = async () => {
    if (passwordDraft.newPassword !== passwordDraft.confirmPassword) {
      setResult({ success: false, message: 'New password and confirmation do not match.' });
      return;
    }

    setSavingPassword(true);
    setResult(null);
    try {
      await changeMyPassword(
        user?.hasPassword === false ? '' : passwordDraft.currentPassword,
        passwordDraft.newPassword
      );
      setPasswordDraft(emptyPasswordState);
      await queryClient.invalidateQueries({ queryKey: authQueryKeys.profile() });
      setPasswordFormOpen(false);
      setResult({ success: true, message: 'Password updated.' });
    } catch (error) {
      setResult({
        success: false,
        message: error instanceof Error ? error.message : 'Failed to update password.',
      });
    } finally {
      setSavingPassword(false);
    }
  };

  const getChannelDraft = (providerKey: string): ChannelDraft =>
    getOwnValue(channelDrafts, providerKey) ?? {
      settings: {},
      channelDefaultTarget: '',
      hitlTetherMode: 'mirror',
    };

  const saveChannel = async (providerKey: string) => {
    setSavingChannelKey(providerKey);
    setResult(null);
    try {
      const draft = getChannelDraft(providerKey);
      if (providerKey === 'telegram' && !draft.settings.channel_device_id?.trim()) {
        setResult({
          success: false,
          message: 'Enter the Telegram private chat ID before connecting. The guided setup explains how to retrieve it from Telegram’s official Bot API.',
        });
        return;
      }
      const selected = draft.channelDefaultTarget.trim();
      const [defaultTargetType, ...targetParts] = selected.split(':');
      const defaultTargetId = targetParts.join(':');

      const updated = await saveMyChannelSettings(providerKey, {
        settings: draft.settings,
        defaultTargetType: defaultTargetType || null,
        defaultTargetId: defaultTargetId || null,
        hitlTetherMode: draft.hitlTetherMode,
      });
      const savedChannel = preferChatChannel(updated.channels, providerKey);
      if (savedChannel) {
        setChannelSettings((previous) => withRecordValue(previous, providerKey, savedChannel));
        setChannelDrafts((previous) => {
          const previousDraft = getOwnValue(previous, providerKey) ?? draft;
          return withRecordValue(previous, providerKey, {
            settings: Object.fromEntries(
              Object.keys(previousDraft.settings).map((key) => [
                key,
                getOwnString(savedChannel.settings, key) ??
                  getOwnValue<string>(previousDraft.settings, key) ??
                  '',
              ])
            ),
            channelDefaultTarget:
              savedChannel.defaultTargetType && savedChannel.defaultTargetId
                ? `${savedChannel.defaultTargetType}:${savedChannel.defaultTargetId}`
                : '',
            hitlTetherMode: savedChannel.hitlTetherMode ?? draft.hitlTetherMode,
          });
        });
      }
      const provider = providers.find((item) => item.providerKey === providerKey);
      const outcome = getChannelSetupOutcome(
        provider?.displayName ?? providerKey,
        updated.channelSetup
      );
      setResult(outcome);
      if (providerKey === 'slack' && outcome.success) {
        const pairing = await startMySlackPairing();
        setSlackPairingCode(pairing.code);
        setSlackPairingFailureReason(null);
        setSlackPairingStatusError(false);
        setSlackPaired(false);
        setChannelDrafts((previous) => {
          const current = getOwnValue(previous, 'slack');
          return current
            ? withRecordValue<ChannelDraft>(previous, 'slack', {
                ...current,
                settings: { ...current.settings, app_token: '', bot_token: '' },
              })
            : previous;
        });
        setResult({
          success: true,
          message: 'Slack tokens checked. Open Slack and send the six-digit code to your Kautuka bot.',
        });
      }
    } catch (error) {
      setResult({
        success: false,
        message: error instanceof Error ? error.message : 'Failed to save channel settings.',
      });
    } finally {
      setSavingChannelKey(null);
    }
  };

  const sendChannelTest = async (providerKey: string) => {
    setSavingChannelKey(providerKey);
    setResult(null);
    try {
      if (providerKey === 'slack') {
        await sendMySlackTestMessage();
        setResult({ success: true, message: 'Slack test message sent.' });
        return;
      }

      const draft = getChannelDraft(providerKey);
      const currentChannel = getOwnValue(channelSettings, providerKey);
      const selected = draft.channelDefaultTarget.trim();
      const [defaultTargetType, ...targetParts] = selected.split(':');
      const defaultTargetId = targetParts.join(':');

      await sendMyChannelHitlTest(providerKey, {
        chatId:
          providerKey === 'slack'
            ? getOwnString(currentChannel?.settings ?? {}, 'slack_dm_channel_id') ??
              ''
            : typeof draft.settings.channel_device_id === 'string'
              ? draft.settings.channel_device_id
            : '',
        settings: draft.settings,
        defaultTargetType: defaultTargetType || null,
        defaultTargetId: defaultTargetId || null,
        hitlTetherMode: draft.hitlTetherMode,
      });
      setResult({ success: true, message: `${providerKey} test sent.` });
    } catch (error) {
      setResult({
        success: false,
        message: error instanceof Error ? error.message : 'Failed to send channel test.',
      });
    } finally {
      setSavingChannelKey(null);
    }
  };

  const copySetupArtifact = async (providerKey: string, label: string, content: string) => {
    try {
      await navigator.clipboard.writeText(content);
      setResult({ success: true, message: `${label} copied. Paste it into the provider portal.` });
      return true;
    } catch {
      setResult({
        success: false,
        message: `Your browser blocked clipboard access. Open the provider portal and configure ${providerKey} using the steps below.`,
      });
      return false;
    }
  };

  const refreshChannels = async () => {
    const refreshed = await getMyChannelSettings();
    setChannelSettings(
      Object.fromEntries(refreshed.channels.map((channel) => [channel.providerKey, channel]))
    );
  };

  const transitionChannel = async (
    providerKey: string,
    transition: 'suspend' | 'recover' | 'remove'
  ) => {
    setSavingChannelKey(providerKey);
    setResult(null);
    try {
      const lifecycle = await transitionMyChannelLifecycle(providerKey, transition);
      await refreshChannels();
      const message =
        transition === 'suspend'
          ? 'Channel suspended. New inbound and outbound work is stopped and its runtime credentials were released.'
          : transition === 'remove'
            ? 'Channel removed. Credentials were deleted and provider traffic is stopped. Save & Connect again to set it up from scratch.'
            : 'Channel moved to recovery. Replace credentials if needed, then select Save & Connect to validate it before activation.';
      setResult({ success: lifecycle.accepted, message });
    } catch (error) {
      setResult({
        success: false,
        message: error instanceof Error ? error.message : 'Failed to update channel status.',
      });
    } finally {
      setSavingChannelKey(null);
    }
  };

  const beginCredentialRotation = (provider: ChannelProviderDefinition) => {
    setChannelDrafts((previous) => {
      const draft = getOwnValue(previous, provider.providerKey) ?? getChannelDraft(provider.providerKey);
      return withRecordValue(previous, provider.providerKey, {
        ...draft,
        settings: Object.fromEntries(
          Object.entries(draft.settings).map(([key, value]) => [
            key,
            provider.fields.some((field) => field.fieldKey === key && field.secret) ? '' : value,
          ])
        ),
      });
    });
    setResult({
      success: true,
      message: `Paste the replacement ${provider.displayName} credential, then select Save & Connect. Revoke the old credential only after verification succeeds.`,
    });
  };

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <div className="w-full">
        <DialogTrigger asChild>
          <button type="button" className={triggerClassName} title="Open profile">
            {collapsed ? (
              <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-primary-fill text-xs font-bold text-primary-foreground shadow-sm">
                {userInitials}
              </div>
            ) : (
              <div className="flex min-w-0 flex-1 items-center gap-3 text-left">
                <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-primary-fill text-xs font-bold text-primary-foreground shadow-sm">
                  {userInitials}
                </div>
                <div className="min-w-0 flex-1">
                  <p className="truncate text-[13px] font-semibold text-foreground">{userName}</p>
                  <p className="truncate text-[11px] uppercase tracking-wider text-muted-foreground">
                    {userRole}
                  </p>
                </div>
              </div>
            )}
          </button>
        </DialogTrigger>
      </div>
      <DialogContent className="flex h-[85vh] max-h-[85vh] flex-col overflow-hidden sm:max-w-3xl">
        <DialogHeader className="shrink-0">
          <DialogTitle className="flex items-center gap-2">
            <UserRound className="h-5 w-5" />
            Your Profile
          </DialogTitle>
          <DialogDescription>
            Manage your account details, password, and channel configuration.
          </DialogDescription>
        </DialogHeader>

        {result && <Banner success={result.success} message={result.message} />}

        <Tabs defaultValue="account" className="flex min-h-0 flex-1 flex-col overflow-hidden">
          <TabsList aria-label="Profile settings" className="shrink-0">
            <TabsTrigger value="account">Account</TabsTrigger>
            <TabsTrigger value="channels">Channels</TabsTrigger>
          </TabsList>
          <TabsContent value="account" className="min-h-0 flex-1 overflow-y-auto">
        <div className="space-y-4">
          <section className="workspace-block space-y-4">
            <div className="flex items-start gap-3">
              <div className="rounded-lg bg-accent p-2 text-primary">
                <UserRound className="h-5 w-5" />
              </div>
              <div>
                <h3 className="text-base font-semibold">Account Details</h3>
                <p className="mt-0.5 text-sm text-muted-foreground">
                  Update the name and email shown for your account.
                </p>
              </div>
            </div>

            <div className="space-y-1">
              <label className="block text-xs text-muted-foreground" htmlFor="profile-username">
                Username
              </label>
              <Input id="profile-username" value={user?.username ?? ''} disabled />
            </div>

            <div className="space-y-1">
              <label className="block text-xs text-muted-foreground" htmlFor="profile-display-name">
                Display Name
              </label>
              <Input
                id="profile-display-name"
                required
                aria-invalid={!profileDraft.displayName.trim()}
                value={profileDraft.displayName}
                onChange={(event) =>
                  setProfileDraft((previous) => ({ ...previous, displayName: event.target.value }))
                }
                placeholder="How your name appears in Kautuka"
              />
              {!profileDraft.displayName.trim() ? (
                <p className="text-xs text-destructive">Display name is required.</p>
              ) : null}
            </div>

            <div className="space-y-1">
              <label className="block text-xs text-muted-foreground" htmlFor="profile-email">
                Email
              </label>
              <Input
                id="profile-email"
                type="email"
                aria-invalid={Boolean(
                  profileDraft.email.trim() &&
                    validateProfileDraft(profileDraft.displayName, profileDraft.email).email
                )}
                value={profileDraft.email}
                onChange={(event) =>
                  setProfileDraft((previous) => ({ ...previous, email: event.target.value }))
                }
                placeholder="name@example.com"
              />
              {validateProfileDraft(profileDraft.displayName, profileDraft.email).email ? (
                <p className="text-xs text-destructive">Enter a valid email address.</p>
              ) : null}
            </div>

            <ExternalSignInMethods
              linkedProviders={user?.externalProviders ?? []}
              onError={(message) => setResult({ success: false, message })}
            />

            <div className="flex justify-end">
              <Button onClick={() => void saveProfile()} disabled={savingProfile}>
                {savingProfile ? 'Saving…' : 'Save Profile'}
              </Button>
            </div>
          </section>

          <section className="workspace-block space-y-4">
            <div className="flex items-start gap-3">
              <div className="rounded-lg bg-accent p-2 text-primary">
                <Settings2 className="h-5 w-5" />
              </div>
              <div>
                <h3 className="text-base font-semibold">Password</h3>
                <p className="mt-0.5 text-sm text-muted-foreground">
                  {user?.hasPassword === false
                    ? 'Set a local password so you can sign in if the linked provider is unavailable.'
                    : 'Update your local sign-in password.'}
                </p>
              </div>
            </div>

            {!passwordFormOpen ? (
              <div className="flex justify-end">
                <Button
                  variant="secondary"
                  onClick={() => {
                    setPasswordDraft(emptyPasswordState);
                    setPasswordFormOpen(true);
                  }}
                >
                  {user?.hasPassword === false ? 'Set password' : 'Change password'}
                </Button>
              </div>
            ) : (
              <>
            {user?.hasPassword === false ? null : (
              <div className="space-y-1">
                <label className="block text-xs text-muted-foreground" htmlFor="current-password">
                  Current Password
                </label>
                <Input
                  id="current-password"
                  type={concealedTextInputType}
                  autoComplete="current-password"
                  value={passwordDraft.currentPassword}
                  onChange={(event) =>
                    setPasswordDraft((previous) => ({
                      ...previous,
                      currentPassword: event.target.value,
                    }))
                  }
                />
              </div>
            )}

            <div className="space-y-1">
              <label className="block text-xs text-muted-foreground" htmlFor="new-password">
                New Password
              </label>
              <Input
                id="new-password"
                type={concealedTextInputType}
                autoComplete="new-password"
                value={passwordDraft.newPassword}
                onChange={(event) =>
                  setPasswordDraft((previous) => ({ ...previous, newPassword: event.target.value }))
                }
              />
            </div>

            <div className="space-y-1">
              <label className="block text-xs text-muted-foreground" htmlFor="confirm-password">
                Confirm New Password
              </label>
              <Input
                id="confirm-password"
                type={concealedTextInputType}
                autoComplete="new-password"
                value={passwordDraft.confirmPassword}
                onChange={(event) =>
                  setPasswordDraft((previous) => ({
                    ...previous,
                    confirmPassword: event.target.value,
                  }))
                }
              />
            </div>

            <div className="flex justify-end">
              <Button
                variant="outline"
                onClick={() => {
                  setPasswordDraft(emptyPasswordState);
                  setPasswordFormOpen(false);
                }}
                disabled={savingPassword}
              >
                Cancel
              </Button>
              {(user?.externalProviders ?? []).map((provider) => (
                <Button
                  key={provider}
                  variant="outline"
                  disabled={savingPassword}
                  onClick={() => {
                    const run = async () => {
                      setSavingPassword(true);
                      setResult(null);
                      try {
                        await clearMyExternalLogin(provider);
                        await queryClient.invalidateQueries({ queryKey: authQueryKeys.profile() });
                        setResult({ success: true, message: `Removed ${provider} sign-in.` });
                      } catch (error) {
                        setResult({
                          success: false,
                          message:
                            error instanceof Error ? error.message : 'Failed to remove sign-in link.',
                        });
                      } finally {
                        setSavingPassword(false);
                      }
                    };
                    void run();
                  }}
                >
                  Remove {provider} sign-in
                </Button>
              ))}
              <Button onClick={() => void savePassword()} disabled={savingPassword}>
                {savingPassword ? 'Updating…' : user?.hasPassword === false ? 'Set Password' : 'Update Password'}
              </Button>
            </div>
              </>
            )}
          </section>
        </div>
          </TabsContent>
          <TabsContent value="channels" className="flex min-h-0 flex-1 flex-col overflow-hidden">

        <section className="workspace-block flex min-h-0 flex-1 flex-col space-y-4 overflow-hidden">
          <div className="flex items-start gap-3">
            <div className="rounded-lg bg-accent p-2 text-primary">
              <Settings2 className="h-5 w-5" />
            </div>
            <div>
              <h3 className="text-base font-semibold">Channels</h3>
              <p className="mt-0.5 text-sm text-muted-foreground">
                Configure each outbound channel with provider-defined settings, default routing, and
                approval tethering.
              </p>
            </div>
          </div>

          <Tabs value={selectedChannelTab || providers[0]?.providerKey || ''} onValueChange={setSelectedChannelTab} className="flex min-h-0 flex-1 flex-col overflow-hidden">
            <TabsList aria-label="Channel settings" className="shrink-0">
              {providers.map((provider) => (
                <TabsTrigger key={provider.providerKey} value={provider.providerKey}>{provider.displayName}</TabsTrigger>
              ))}
            </TabsList>
          {loadingChannel ? (
            <p className="min-h-0 flex-1 overflow-y-auto text-sm text-muted-foreground">Loading channel settings…</p>
          ) : (
            <div className="min-h-0 flex-1 overflow-y-auto">
              {providers.map((provider) => {
                const draft = getChannelDraft(provider.providerKey);
                const currentChannel = getOwnValue(channelSettings, provider.providerKey);
                const guide = getChannelSetupGuide(provider.providerKey);
                const channelStatus = currentChannel?.status ?? 'INACTIVE';
                const hasCurrentTarget =
                  draft.channelDefaultTarget.length > 0 &&
                  agents
                    .map((agent) => `agent:${agent.id}`)
                    .includes(draft.channelDefaultTarget) === false;
                const isSlack = provider.providerKey === 'slack';
                const visibleFields = isSlack
                  ? []
                  : provider.fields;
                const slackTokenKeys = currentChannel?.configuredSecretFieldKeys ?? [];
                const slackAppToken = draft.settings.app_token ?? '';
                const slackBotToken = draft.settings.bot_token ?? '';
                const slackAppTokenValid = validateSlackAppToken(slackAppToken);
                const slackBotTokenValid = validateSlackBotToken(slackBotToken);
                const slackAppTokenReady = slackAppTokenValid &&
                  (Boolean(slackAppToken.trim()) || slackTokenKeys.includes('app_token'));
                const slackBotTokenReady = slackBotTokenValid &&
                  (Boolean(slackBotToken.trim()) || slackTokenKeys.includes('bot_token'));
                const slackTokensReady = slackAppTokenReady && slackBotTokenReady;
                const slackAppId = getOwnString(currentChannel?.settings ?? {}, 'app_id') ??
                  (/^A[A-Z0-9]+$/.test(slackAppIdOverride.trim()) ? slackAppIdOverride.trim() : null);
                const slackTeamId = getOwnString(currentChannel?.settings ?? {}, 'team_id');
                const slackOpenUrl = slackAppId
                  ? `https://slack.com/app_redirect?${new URLSearchParams({
                      app: slackAppId,
                      ...(slackTeamId ? { team: slackTeamId } : {}),
                    }).toString()}`
                  : 'https://api.slack.com/apps';

                return (
                  <TabsContent key={provider.providerKey} value={provider.providerKey} className="mt-2">
                  <div
                    className="rounded-lg border border-border/60 p-4"
                  >
                    <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
                      <div>
                        <h4 className="text-sm font-semibold">{provider.displayName}</h4>
                        <p className="mt-1 max-w-2xl text-xs text-muted-foreground">{guide.intro}</p>
                        <p className="mt-2 flex items-center gap-1 text-[11px] text-muted-foreground">
                          {currentChannel?.registrationId && channelStatus === 'ACTIVE' ? (
                            <CheckCircle2 className="h-3.5 w-3.5 text-done" />
                          ) : currentChannel?.registrationId && channelStatus === 'SUSPENDED' ? (
                            <PauseCircle className="h-3.5 w-3.5 text-attention" />
                          ) : currentChannel?.registrationId ? (
                            <RotateCcw className="h-3.5 w-3.5 text-attention" />
                          ) : null}
                          {currentChannel?.registrationId
                            ? `Status: ${channelStatus.toLowerCase().split('_').join(' ')}`
                            : 'Not connected yet'}
                        </p>
                      </div>
                      <ChannelSetupGuidePanel
                        guide={guide}
                        expanded={!currentChannel?.registrationId}
                        onOpenPortal={isSlack ? getMySlackAppCreationUrl : undefined}
                        onCopyArtifact={(label, content) =>
                          copySetupArtifact(provider.providerKey, label, content)
                        }
                      />
                    </div>

                    {isSlack ? (
                      <div className="mb-4 space-y-3 rounded-md border border-border/60 bg-muted/20 p-3">
                        {slackPaired ? (
                          <div className="flex items-center gap-2 text-sm font-semibold text-done">
                            <CheckCircle2 className="h-4 w-4" aria-hidden="true" />
                            Slack is connected to your account.
                          </div>
                        ) : (
                          <>
                            <div className="space-y-4">
                              <div className="space-y-1">
                                <label className="block text-xs font-semibold text-foreground" htmlFor="slack-app-token">
                                  App-level token <code>(xapp-)</code>
                                </label>
                                <p className="text-[11px] text-muted-foreground">
                                  In Slack, open your app → <strong>Basic Information</strong> → scroll to <strong>App-Level Tokens</strong> → <strong>Generate Token</strong>. Name the token, add <code>connections:write</code>, generate it, then copy the <code>xapp-</code> value.
                                </p>
                                <Input
                                  id="slack-app-token"
                                  type="password"
                                  autoComplete="off"
                                  aria-invalid={!slackAppTokenValid}
                                  value={slackAppToken}
                                  placeholder={slackTokenKeys.includes('app_token') ? 'Configured. Leave blank to keep it.' : 'Paste xapp- token'}
                                  onChange={(event) => {
                                    const token = event.target.value.trim();
                                    setChannelDrafts((previous) => {
                                      const current = getOwnValue(previous, 'slack') ?? getChannelDraft('slack');
                                      return withRecordValue<ChannelDraft>(previous, 'slack', {
                                        ...current,
                                        settings: { ...current.settings, app_token: token },
                                      });
                                    });
                                  }}
                                />
                                {!slackAppTokenValid ? (
                                  <p className="text-[11px] text-destructive" role="alert">
                                    App-level tokens start with <code>xapp-</code>. Paste the token from App-Level Tokens.
                                  </p>
                                ) : null}
                              </div>
                              <div className="space-y-1">
                                <label className="block text-xs font-semibold text-foreground" htmlFor="slack-bot-token">
                                  Bot user token <code>(xoxb-)</code>
                                </label>
                                <p className="text-[11px] text-muted-foreground">
                                  Open your app → <strong>OAuth &amp; Permissions</strong>. Install or reinstall the app to your workspace if prompted, then find <strong>Bot User OAuth Token</strong> and copy the <code>xoxb-</code> value.
                                </p>
                                <Input
                                  id="slack-bot-token"
                                  type="password"
                                  autoComplete="off"
                                  aria-invalid={!slackBotTokenValid}
                                  value={slackBotToken}
                                  placeholder={slackTokenKeys.includes('bot_token') ? 'Configured. Leave blank to keep it.' : 'Paste xoxb- token'}
                                  onChange={(event) => {
                                    const token = event.target.value.trim();
                                    setChannelDrafts((previous) => {
                                      const current = getOwnValue(previous, 'slack') ?? getChannelDraft('slack');
                                      return withRecordValue<ChannelDraft>(previous, 'slack', {
                                        ...current,
                                        settings: { ...current.settings, bot_token: token },
                                      });
                                    });
                                  }}
                                />
                                {!slackBotTokenValid ? (
                                  <p className="text-[11px] text-destructive" role="alert">
                                    Bot user tokens start with <code>xoxb-</code>. Paste the token from OAuth &amp; Permissions.
                                  </p>
                                ) : null}
                              </div>
                            </div>
                              <div className="flex flex-wrap gap-x-5 gap-y-1 text-[11px]" aria-label="Slack token progress">
                                <span className={slackAppTokenReady ? 'flex items-center gap-1 text-done' : 'text-muted-foreground'}>
                                  {slackAppTokenReady ? <CheckCircle2 className="h-3 w-3" aria-hidden="true" /> : null}
                                  App-level token {slackAppTokenReady ? 'received' : 'needed'}
                                </span>
                                <span className={slackBotTokenReady ? 'flex items-center gap-1 text-done' : 'text-muted-foreground'}>
                                  {slackBotTokenReady ? <CheckCircle2 className="h-3 w-3" aria-hidden="true" /> : null}
                                  Bot token {slackBotTokenReady ? 'received' : 'needed'}
                                </span>
                              </div>
                            {slackTokensReady && !slackPairingCode ? (
                              <p className="text-[11px] text-muted-foreground">Both tokens are ready. Select Check tokens and connect; Kautuka checks them with Slack before starting the connection.</p>
                            ) : null}
                            {slackPairingFailureReason ? (
                              <p className="text-[11px] text-destructive" role="status">{slackPairingFailureReason}</p>
                            ) : null}
                            {slackPairingCode ? (
                              <div className="flex flex-wrap items-center justify-between gap-3 rounded border border-primary/30 bg-background p-3">
                                <div>
                                  <p className="text-xs font-semibold">Send this code to your Kautuka bot in Slack</p>
                                  <p className="mt-1 font-mono text-2xl font-bold tracking-[0.25em]" aria-label={`Pairing code ${slackPairingCode}`}>{slackPairingCode}</p>
                                  <p className="mt-1 text-[11px] text-muted-foreground">{slackAppId ? 'This opens a direct message with the Kautuka bot. Send the code there; it expires in five minutes.' : 'Open your Slack app list, choose your Kautuka app, then send the code to its bot. The code expires in five minutes.'} This page will link your account after the bot receives it.</p>
                                  {slackPairingStatusError ? (
                                    <p className="mt-2 text-[11px] text-destructive" role="status">Kautuka couldn’t check whether Slack received the code. It will keep trying automatically.</p>
                                  ) : null}
                                  {!getOwnString(currentChannel?.settings ?? {}, 'app_id') ? (
                                    <div className="mt-2 space-y-1">
                                      <label className="block text-[11px] font-semibold" htmlFor="slack-app-id">Slack App ID</label>
                                      <Input
                                        id="slack-app-id"
                                        value={slackAppIdOverride}
                                        onChange={(event) => setSlackAppIdOverride(event.target.value.trim().toUpperCase())}
                                        placeholder="A… (from Basic Information)"
                                        autoComplete="off"
                                      />
                                      <p className="text-[11px] text-muted-foreground">On the Slack app page, open <strong>Basic Information</strong> and copy the <strong>App ID</strong> near the top.</p>
                                    </div>
                                  ) : null}
                                </div>
                                <Button variant="secondary" asChild>
                                  <a href={slackOpenUrl} target="_blank" rel="noopener noreferrer">
                                    {slackAppId ? 'Open Kautuka in Slack' : 'Open Slack app list'}
                                  </a>
                                </Button>
                              </div>
                            ) : null}
                          </>
                        )}
                      </div>
                    ) : null}

                    <div className="grid grid-cols-1 gap-3">
                      {visibleFields.map((field) => {
                        const fieldId = `${provider.providerKey}-${field.fieldKey}`;
                        const currentValue = getOwnValue(draft.settings, field.fieldKey) ?? '';
                        const isDestinationField = field.fieldKey === 'channel_device_id';
                        const isTelegramChatId =
                          provider.providerKey === 'telegram' && isDestinationField;
                        const inputType = field.secret ? concealedTextInputType : 'text';
                        const label = isDestinationField ? guide.destinationLabel : field.label;
                        const description = isDestinationField
                          ? guide.destinationHelp
                          : field.description;
                        return (
                          <div key={fieldId} className="space-y-1">
                            <label
                              className="block text-xs font-semibold text-foreground"
                              htmlFor={fieldId}
                            >
                              {label}
                              {field.required || isTelegramChatId ? ' *' : ''}
                            </label>
                            {description && (
                              <p className="text-[11px] text-muted-foreground">{description}</p>
                            )}
                            {field.fieldType === 'ATTACHMENT_SCHEMA_FIELD_TYPE_ENUM_SINGLE' ? (
                              <select
                                id={fieldId}
                                className="input"
                                value={String(currentValue)}
                                onChange={(event) =>
                                  setChannelDrafts((previous) => ({
                                    ...previous,
                                    [provider.providerKey]: {
                                      ...getChannelDraft(provider.providerKey),
                                      settings: {
                                        ...getChannelDraft(provider.providerKey).settings,
                                        [field.fieldKey]: event.target.value,
                                      },
                                    },
                                  }))
                                }
                              >
                                <option value="">Choose…</option>
                                {field.enumValues.map((option) => (
                                  <option key={option} value={option}>
                                    {option}
                                  </option>
                                ))}
                              </select>
                            ) : (
                              <Input
                                id={fieldId}
                                type={inputType}
                                value={String(currentValue)}
                                onChange={(event) =>
                                  setChannelDrafts((previous) => ({
                                    ...previous,
                                    [provider.providerKey]: {
                                      ...getChannelDraft(provider.providerKey),
                                      settings: {
                                        ...getChannelDraft(provider.providerKey).settings,
                                        [field.fieldKey]: event.target.value,
                                      },
                                    },
                                  }))
                                }
                                placeholder={
                                  field.secret
                                    ? currentChannel?.configuredSecretFieldKeys?.includes(
                                        field.fieldKey
                                      )
                                      ? 'Configured. Leave blank to keep current value.'
                                      : 'Paste secret value'
                                    : label
                                }
                              />
                            )}
                          </div>
                        );
                      })}

                      <div className="space-y-1">
                        <label
                          className="block text-xs font-semibold text-foreground"
                          htmlFor={`${provider.providerKey}-default-target`}
                        >
                          Default Kautuka target
                        </label>
                        <select
                          id={`${provider.providerKey}-default-target`}
                          className="input"
                          value={draft.channelDefaultTarget}
                          onChange={(event) =>
                            setChannelDrafts((previous) => ({
                              ...previous,
                              [provider.providerKey]: {
                                ...getChannelDraft(provider.providerKey),
                                channelDefaultTarget: event.target.value,
                              },
                            }))
                          }
                        >
                          <option value="">Choose default target...</option>
                          {hasCurrentTarget && (
                            <option value={draft.channelDefaultTarget}>
                              Current target: {draft.channelDefaultTarget}
                            </option>
                          )}
                          {agents.map((agent) => (
                            <option
                              key={`${provider.providerKey}-${agent.id}`}
                              value={`agent:${agent.id}`}
                            >
                              Agent: {agent.name || agent.id}
                            </option>
                          ))}
                        </select>
                      </div>

                      <div className="space-y-1">
                        <label
                          className="block text-xs font-semibold text-foreground"
                          htmlFor={`${provider.providerKey}-tether-mode`}
                        >
                          Channel sync
                        </label>
                        <select
                          id={`${provider.providerKey}-tether-mode`}
                          className="input"
                          value={draft.hitlTetherMode}
                          onChange={(event) =>
                            setChannelDrafts((previous) => ({
                              ...previous,
                              [provider.providerKey]: {
                                ...getChannelDraft(provider.providerKey),
                                hitlTetherMode: event.target.value as 'hitl' | 'mirror',
                              },
                            }))
                          }
                        >
                          <option value="hitl">Approvals + final reply</option>
                          <option value="mirror">Mirror completed replies</option>
                        </select>
                      </div>
                    </div>

                    <div className="mt-4 flex flex-wrap justify-end gap-2">
                      {currentChannel?.registrationId && channelStatus === 'ACTIVE' ? (
                        <Button
                          variant="secondary"
                          onClick={() => void transitionChannel(provider.providerKey, 'suspend')}
                          disabled={savingChannelKey !== null}
                        >
                          <PauseCircle className="mr-1 h-4 w-4" /> Suspend
                        </Button>
                      ) : null}
                      {currentChannel?.registrationId &&
                      ['SUSPENDED', 'ERROR', 'RECOVERY_REQUIRED', 'DELETED'].includes(channelStatus) ? (
                        <Button
                          variant="default"
                          onClick={() => void transitionChannel(provider.providerKey, 'recover')}
                          disabled={savingChannelKey !== null}
                        >
                          <RotateCcw className="mr-1 h-4 w-4" /> Start recovery
                        </Button>
                      ) : null}
                      {currentChannel?.registrationId &&
                      ['ACTIVE', 'INACTIVE', 'SUSPENDED', 'RECOVERY_REQUIRED'].includes(channelStatus) ? (
                        <Button
                          variant="destructive"
                          onClick={() => void transitionChannel(provider.providerKey, 'remove')}
                          disabled={savingChannelKey !== null}
                        >
                          <Trash2 className="mr-1 h-4 w-4" /> Remove
                        </Button>
                      ) : null}
                      {currentChannel?.registrationId &&
                      ['ACTIVE', 'INACTIVE'].includes(channelStatus) ? (
                        <Button
                          variant="secondary"
                          onClick={() => beginCredentialRotation(provider)}
                          disabled={savingChannelKey !== null}
                        >
                          <RotateCcw className="mr-1 h-4 w-4" /> Replace credentials
                        </Button>
                      ) : null}
                      <Button
                        variant="secondary"
                        onClick={() => void sendChannelTest(provider.providerKey)}
                        disabled={
                          savingChannelKey !== null || loadingChannel || channelStatus !== 'ACTIVE' || (isSlack && !slackPaired)
                        }
                      >
                        {isSlack ? 'Send test message' : 'Verify with Test Prompt'}
                      </Button>
                      <Button
                        onClick={() => void saveChannel(provider.providerKey)}
                        disabled={
                          savingChannelKey !== null ||
                          loadingChannel ||
                          (isSlack && !slackTokensReady) ||
                          ['SUSPENDED', 'ERROR', 'RECOVERY_REQUIRED', 'DELETED'].includes(
                            channelStatus
                          )
                        }
                      >
                        {savingChannelKey === provider.providerKey
                          ? 'Checking tokens…'
                          : isSlack
                            ? 'Check tokens and connect'
                            : 'Save & Connect'}
                      </Button>
                    </div>
                    <p className="mt-2 text-right text-[11px] text-muted-foreground">
                      {channelStatus === 'DELETED'
                        ? 'Channel removed. Select Start recovery, then Save & Connect to set it up again.'
                        : ['SUSPENDED', 'ERROR', 'RECOVERY_REQUIRED'].includes(channelStatus)
                          ? 'Select Start recovery before replacing credentials or reconnecting.'
                          : guide.afterSave}
                    </p>
                  </div>
                  </TabsContent>
                );
              })}
            </div>
          )}
          </Tabs>
        </section>
          </TabsContent>
        </Tabs>
      </DialogContent>
    </Dialog>
  );
}

function preferChatChannel(channels: UserChannelSettings[], providerKey: string) {
  const matches = channels.filter((item) => item.providerKey === providerKey);
  return (
    matches.find((item) => item.registrationId?.endsWith(':default')) ??
    matches.find(
      (item) =>
        !item.registrationId?.includes(':agent:') && !item.registrationId?.includes(':workflow')
    )
  );
}

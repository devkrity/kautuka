import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { SegmentedControl } from '@/components/ui/segmented-control';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { PackageOpen, Plug, ShieldCheck } from 'lucide-react';
import React from 'react';
import type { AgentSettingsEntry, UserSettingsEntry } from '../types/settingsTypes';

const SECRET_INPUT_TYPE: React.HTMLInputTypeAttribute = ['pass', 'word'].join('');

interface UserAccessCardProps {
  user: UserSettingsEntry;
  agents: AgentSettingsEntry[];
  editing: boolean;
  saving: boolean;
  isAdminDraft: boolean;
  isActiveDraft: boolean;
  canImportConnectorsDraft: boolean;
  canImportSkillsDraft: boolean;
  channelDeviceId: string;
  channelBotToken: string;
  channelDefaultTarget: string;
  getChannelHitlTetherMode: (providerKey: string) => 'hitl' | 'mirror';
  saveBlockedReason: string | null;
  onStartEdit: () => void;
  onCancelEdit: () => void;
  onSetIsAdmin: (value: boolean) => void;
  onSetIsActive: (value: boolean) => void;
  onSetCanImportConnectors: (value: boolean) => void;
  onSetCanImportSkills: (value: boolean) => void;
  onChannelDeviceChange: (value: string) => void;
  onChannelBotTokenChange: (value: string) => void;
  onChannelDefaultTargetChange: (value: string) => void;
  onChannelHitlTetherModeChange: (providerKey: string, value: 'hitl' | 'mirror') => void;
  onSaveChannelSettings: () => void;
  onDispatchChannelHitl: () => void;
  onSave: () => void;
  onClearExternalLogin: (userId: string, provider: string) => Promise<void>;
}

function UserAccessSummary({
  user,
  onClearExternalLogin,
}: Readonly<Pick<UserAccessCardProps, 'user' | 'onClearExternalLogin'>>) {
  return (
    <CardContent className="space-y-1 pt-0 text-sm text-muted-foreground">
      <div className="mb-2 flex flex-wrap gap-2">
        <span className="badge border-border bg-muted text-foreground">Role {user.role}</span>
      </div>
      <p>Telegram chat: {user.channelDeviceId || 'not set'}</p>
      <p>Telegram bot token: {user.channelBotTokenSet ? 'configured' : 'not configured'}</p>
      <p>
        Telegram default target:{' '}
        {user.channelDefaultTargetType && user.channelDefaultTargetId
          ? `${user.channelDefaultTargetType}:${user.channelDefaultTargetId}`
          : 'not set'}
      </p>
      {(user.channelTetherModes ?? []).map((item) => (
        <p key={`${item.registrationId}:${item.providerKey}`}>{item.providerKey} channel sync: {item.hitlTetherMode}</p>
      ))}
      {(user.externalProviders ?? []).map((provider) => (
        <div key={provider} className="flex items-center justify-between gap-2 pt-2">
          <span>Linked {provider}</span>
          <Button
            type="button"
            variant="destructive"
            size="sm"
            onClick={() => {
              onClearExternalLogin(user.id, provider).catch(() => undefined);
            }}
          >
            Remove
          </Button>
        </div>
      ))}
    </CardContent>
  );
}

function UserAccessEditor({
  user,
  agents,
  saving,
  isAdminDraft,
  isActiveDraft,
  canImportConnectorsDraft,
  canImportSkillsDraft,
  channelDeviceId,
  channelBotToken,
  channelDefaultTarget,
  getChannelHitlTetherMode,
  saveBlockedReason,
  onCancelEdit,
  onSetIsAdmin,
  onSetIsActive,
  onSetCanImportConnectors,
  onSetCanImportSkills,
  onChannelDeviceChange,
  onChannelBotTokenChange,
  onChannelDefaultTargetChange,
  onChannelHitlTetherModeChange,
  onSaveChannelSettings,
  onDispatchChannelHitl,
  onSave,
}: Readonly<Omit<UserAccessCardProps, 'editing' | 'onStartEdit' | 'onClearExternalLogin'>>) {
  const isStandardDraft = isAdminDraft === false;
  const hasCurrentTarget =
    channelDefaultTarget.length > 0 &&
    agents.map((agent) => `agent:${agent.id}`).includes(channelDefaultTarget) === false;

  return (
    <CardContent className="space-y-4">
      <div className="space-y-2">
        <p className="block text-xs text-muted-foreground">Access</p>
        <div className="flex flex-col gap-3 md:flex-row md:items-center">
          <SegmentedControl
            shape="pill"
            size="md"
            value={isAdminDraft ? 'admin' : 'user'}
            onChange={(next) => onSetIsAdmin(next === 'admin')}
            options={[
              { value: 'user', label: 'User', tone: 'neutral' },
              { value: 'admin', label: 'Admin', tone: 'done' },
            ]}
          />
        </div>
      </div>

      <div className="space-y-2">
        <p className="block text-xs text-muted-foreground">Account Status</p>
        <SegmentedControl
          shape="pill"
          size="md"
          value={isActiveDraft ? 'active' : 'inactive'}
          onChange={(next) => onSetIsActive(next === 'active')}
          options={[
            { value: 'active', label: 'Active', tone: 'done' },
            { value: 'inactive', label: 'Inactive', tone: 'neutral' },
          ]}
        />
      </div>

      <div className="settings-subpanel-compact space-y-2">
        <p className="block text-xs text-muted-foreground">Import Permissions</p>
        <p className="text-xs text-muted-foreground">
          Controls whether this user can import connector bundles and skill packages.
        </p>
        <div className="space-y-3">
          <div className="rounded-xl border border-border/50 bg-card overflow-hidden shadow-sm">
            <div className="flex items-center gap-3 px-4 py-3">
              <div className="h-7 w-7 rounded-lg bg-muted flex items-center justify-center shrink-0">
                <Plug className="h-3.5 w-3.5 text-muted-foreground" />
              </div>
              <span className="text-sm font-medium flex-1 truncate">Can import connectors</span>
              <SegmentedControl
                className="shrink-0"
                value={canImportConnectorsDraft ? 'enabled' : 'disabled'}
                onChange={(next) => onSetCanImportConnectors(next === 'enabled')}
                options={[
                  { value: 'disabled', label: 'Disabled', tone: 'neutral' },
                  { value: 'enabled', label: 'Enabled', tone: 'primary' },
                ]}
              />
            </div>
          </div>

          <div className="rounded-xl border border-border/50 bg-card overflow-hidden shadow-sm">
            <div className="flex items-center gap-3 px-4 py-3">
              <div className="h-7 w-7 rounded-lg bg-muted flex items-center justify-center shrink-0">
                <PackageOpen className="h-3.5 w-3.5 text-muted-foreground" />
              </div>
              <span className="text-sm font-medium flex-1 truncate">Can import skills</span>
              <SegmentedControl
                className="shrink-0"
                value={canImportSkillsDraft ? 'enabled' : 'disabled'}
                onChange={(next) => onSetCanImportSkills(next === 'enabled')}
                options={[
                  { value: 'disabled', label: 'Disabled', tone: 'neutral' },
                  { value: 'enabled', label: 'Enabled', tone: 'primary' },
                ]}
              />
            </div>
          </div>
        </div>
      </div>

      <div className="settings-subpanel-compact space-y-3">
        <p className="block text-xs text-muted-foreground">Telegram Settings</p>
        <p className="text-xs text-muted-foreground">
          Configure the private chat and fallback agent for this user. Agents and sessions are switched from the chat keyboard.
        </p>
        <div className="grid grid-cols-1 gap-2 md:grid-cols-4">
          <div className="space-y-1">
            <label
              className="block text-xs text-muted-foreground"
              htmlFor={`telegram-device-${user.id}`}
            >
              Device ID
            </label>
            <Input
              id={`telegram-device-${user.id}`}
              value={channelDeviceId}
              onChange={(e) => onChannelDeviceChange(e.target.value)}
              placeholder="Telegram device ID"
            />
          </div>
          <div className="space-y-1">
            <label
              className="block text-xs text-muted-foreground"
              htmlFor={`telegram-token-${user.id}`}
            >
              Bot Token
            </label>
            <Input
              id={`telegram-token-${user.id}`}
              name={`telegram-bot-token-${user.id}`}
              type={SECRET_INPUT_TYPE}
              autoComplete="new-password"
              value={channelBotToken}
              onChange={(e) => onChannelBotTokenChange(e.target.value)}
              placeholder={
                user.channelBotTokenSet ? 'leave blank to keep current bot token' : 'bot token'
              }
            />
          </div>
          <div className="space-y-1">
            <label
              className="block text-xs text-muted-foreground"
              htmlFor={`telegram-target-${user.id}`}
            >
              Default Chat Channel
            </label>
            <select
              id={`telegram-target-${user.id}`}
              title="Default telegram target"
              className="input"
              value={channelDefaultTarget}
              onChange={(e) => onChannelDefaultTargetChange(e.target.value)}
            >
              <option value="">Choose default target...</option>
              {hasCurrentTarget && (
                <option value={channelDefaultTarget}>Current target: {channelDefaultTarget}</option>
              )}
              {agents.map((agent) => (
                <option
                  key={`${user.id}-telegram-default-agent-${agent.id}`}
                  value={`agent:${agent.id}`}
                >
                  Agent: {agent.name || agent.id}
                </option>
              ))}
            </select>
          </div>
          <div className="space-y-1">
            {(user.channelTetherModes ?? []).map((tether) => (
              <div key={`${tether.registrationId}:${tether.providerKey}`} className="space-y-1">
                <label className="block text-xs text-muted-foreground" htmlFor={`${tether.providerKey}-tether-${user.id}`}>
                  {tether.providerKey} channel sync
                </label>
                <select
                  id={`${tether.providerKey}-tether-${user.id}`}
                  title={`${tether.providerKey} channel sync mode`}
                  className="input"
                  value={getChannelHitlTetherMode(tether.providerKey)}
                  onChange={(e) => onChannelHitlTetherModeChange(tether.providerKey, e.target.value as 'hitl' | 'mirror')}
                >
                  <option value="hitl">Approvals + final reply</option>
                  <option value="mirror">Mirror completed replies</option>
                </select>
              </div>
            ))}
          </div>
        </div>
        <div className="flex justify-end">
          <Button variant="secondary" disabled={saving} onClick={onSaveChannelSettings}>
            Save and Sync Telegram
          </Button>
        </div>
      </div>

      <div className="settings-subpanel-compact space-y-3">
        <p className="block text-xs text-muted-foreground">Test Telegram Integration</p>
        <Button variant="secondary" disabled={saving} onClick={onDispatchChannelHitl}>
          Send Telegram Test
        </Button>
      </div>

      <div className="flex items-center justify-end gap-2 border-t border-border pt-3">
        <Button variant="secondary" disabled={saving} onClick={onCancelEdit}>
          Cancel
        </Button>
        <Button disabled={saving || Boolean(saveBlockedReason)} onClick={onSave}>
          Save access changes
        </Button>
      </div>
      {saveBlockedReason && <p className="text-sm text-destructive">{saveBlockedReason}</p>}
    </CardContent>
  );
}

export function UserAccessCard({
  user,
  agents,
  editing,
  saving,
  isAdminDraft,
  isActiveDraft,
  canImportConnectorsDraft,
  canImportSkillsDraft,
  channelDeviceId,
  channelBotToken,
  channelDefaultTarget,
  getChannelHitlTetherMode,
  saveBlockedReason,
  onStartEdit,
  onCancelEdit,
  onSetIsAdmin,
  onSetIsActive,
  onSetCanImportConnectors,
  onSetCanImportSkills,
  onChannelDeviceChange,
  onChannelBotTokenChange,
  onChannelDefaultTargetChange,
  onChannelHitlTetherModeChange,
  onSaveChannelSettings,
  onDispatchChannelHitl,
  onSave,
  onClearExternalLogin,
}: Readonly<UserAccessCardProps>) {
  const bodyContent = editing ? (
    <UserAccessEditor
      user={user}
      agents={agents}
      saving={saving}
      isAdminDraft={isAdminDraft}
      isActiveDraft={isActiveDraft}
      canImportConnectorsDraft={canImportConnectorsDraft}
      canImportSkillsDraft={canImportSkillsDraft}
      channelDeviceId={channelDeviceId}
      channelBotToken={channelBotToken}
      channelDefaultTarget={channelDefaultTarget}
      getChannelHitlTetherMode={getChannelHitlTetherMode}
      saveBlockedReason={saveBlockedReason}
      onCancelEdit={onCancelEdit}
      onSetIsAdmin={onSetIsAdmin}
      onSetIsActive={onSetIsActive}
      onSetCanImportConnectors={onSetCanImportConnectors}
      onSetCanImportSkills={onSetCanImportSkills}
      onChannelDeviceChange={onChannelDeviceChange}
      onChannelBotTokenChange={onChannelBotTokenChange}
      onChannelDefaultTargetChange={onChannelDefaultTargetChange}
      onChannelHitlTetherModeChange={onChannelHitlTetherModeChange}
      onSaveChannelSettings={onSaveChannelSettings}
      onDispatchChannelHitl={onDispatchChannelHitl}
      onSave={onSave}
    />
  ) : (
    <UserAccessSummary user={user} onClearExternalLogin={onClearExternalLogin} />
  );

  return (
    <Card className={`${editing ? 'xl:col-span-2' : ''} card-interactive`}>
      <CardHeader>
        <div className="flex items-start justify-between gap-3">
          <div>
            <div className="mb-2 inline-flex items-center gap-2">
              <ShieldCheck className="h-4 w-4 text-primary" />
              <Badge variant={user.isActive ? 'success' : 'muted'}>
                {user.isActive ? 'Active' : 'Inactive'}
              </Badge>
              <Badge variant="muted">{user.role}</Badge>
            </div>
            <CardTitle>{user.username}</CardTitle>
            <CardDescription>{user.email || 'no email'}</CardDescription>
          </div>
          {editing ? (
            <Button variant="secondary" onClick={onCancelEdit}>
              Cancel
            </Button>
          ) : (
            <Button variant="secondary" onClick={onStartEdit}>
              Edit
            </Button>
          )}
        </div>
      </CardHeader>

      {bodyContent}
    </Card>
  );
}

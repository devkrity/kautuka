import { settingsAdmin } from '@/features/settings';
import { readApiString } from '@/lib/apiResponse';
import { useEffect, useMemo, useState } from 'react';
import {
  ROLE_ADMIN,
  ROLE_MEMBER,
  type AgentSettingsEntry,
  type UserSettingsEntry,
} from '../types/settingsTypes';
import { apiError } from '../utils/settingsUtils';

type RoleFilter = 'all' | 'elevated' | 'standard';
type StatusFilter = 'all' | 'active' | 'inactive';
type InviteRole = 'elevated' | 'standard';

const normalizeUserEntry = (user: UserSettingsEntry): UserSettingsEntry => user;
const normalizeUsername = (value: string): string => value.trim().toLowerCase();

const readRecordValue = <T>(source: Record<string, T>, key: string, fallback: T): T => {
  const value = new Map<string, T>(Object.entries(source)).get(key);
  return value ?? fallback;
};

const withRecordValue = <T>(
  source: Record<string, T>,
  key: string,
  value: T
): Record<string, T> => {
  const next = new Map(Object.entries(source));
  next.set(key, value);
  return Object.fromEntries(next) as Record<string, T>;
};

export function useUsersTab() {
  const [users, setUsers] = useState<UserSettingsEntry[]>([]);
  const [agents, setAgents] = useState<AgentSettingsEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [savingUserId, setSavingUserId] = useState<string | null>(null);
  const [editingUserId, setEditingUserId] = useState<string | null>(null);
  const [result, setResult] = useState<{ success: boolean; message: string } | null>(null);
  const [search, setSearch] = useState('');
  const [roleFilter, setRoleFilter] = useState<RoleFilter>('all');
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('all');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(5);

  const [invite, setInvite] = useState({
    username: '',
    email: '',
    secret: '',
    role: 'standard' as InviteRole,
  });
  const [isAdmin, setIsAdmin] = useState<Record<string, boolean>>({});
  const [isActive, setIsActive] = useState<Record<string, boolean>>({});
  const [canImportConnectors, setCanImportConnectors] = useState<Record<string, boolean>>({});
  const [canImportSkills, setCanImportSkills] = useState<Record<string, boolean>>({});
  const [channelDeviceId, setChannelDeviceId] = useState<Record<string, string>>({});
  const [channelBotToken, setChannelBotToken] = useState<Record<string, string>>({});
  const [channelDefaultTarget, setChannelDefaultTarget] = useState<Record<string, string>>({});
  const [channelHitlTetherMode, setChannelHitlTetherMode] = useState<Record<string, 'hitl' | 'mirror'>>({});

  const load = async () => {
    setLoading(true);
    setResult(null);
    try {
      const [ur, ar] = await Promise.all([settingsAdmin.listUsers(), settingsAdmin.listAgents()]);
      const loadedUsers = (Array.isArray(ur.data.users) ? ur.data.users : []).map(
        normalizeUserEntry
      );
      setUsers(loadedUsers);
      setAgents(ar.data.agents);

      const nextIsAdmin = new Map<string, boolean>();
      const nextIsActive = new Map<string, boolean>();
      const nextCanImportConnectors = new Map<string, boolean>();
      const nextCanImportSkills = new Map<string, boolean>();
      const nextChannelDeviceId = new Map<string, string>();
      const nextChannelBotToken = new Map<string, string>();
      const nextChannelDefaultTarget = new Map<string, string>();
      const nextChannelHitlTetherMode = new Map<string, 'hitl' | 'mirror'>();

      for (const user of loadedUsers) {
        nextIsAdmin.set(user.id, user.role === ROLE_ADMIN);
        nextIsActive.set(user.id, user.isActive);
        nextCanImportConnectors.set(user.id, Boolean(user.canImportConnectors));
        nextCanImportSkills.set(user.id, Boolean(user.canImportSkills));
        nextChannelDeviceId.set(user.id, user.channelDeviceId ?? '');
        nextChannelBotToken.set(user.id, '');

        const defaultTargetType = user.channelDefaultTargetType ?? null;
        const defaultTargetId = user.channelDefaultTargetId ?? null;
        if (defaultTargetType === 'agent' && defaultTargetId) {
          nextChannelDefaultTarget.set(user.id, `agent:${defaultTargetId}`);
        } else {
          nextChannelDefaultTarget.set(user.id, '');
        }
        for (const tether of user.channelTetherModes ?? [])
          nextChannelHitlTetherMode.set(`${user.id}:${tether.providerKey}`, tether.hitlTetherMode);
      }

      setIsAdmin(Object.fromEntries(nextIsAdmin));
      setIsActive(Object.fromEntries(nextIsActive));
      setCanImportConnectors(Object.fromEntries(nextCanImportConnectors));
      setCanImportSkills(Object.fromEntries(nextCanImportSkills));
      setChannelDeviceId(Object.fromEntries(nextChannelDeviceId));
      setChannelBotToken(Object.fromEntries(nextChannelBotToken));
      setChannelDefaultTarget(Object.fromEntries(nextChannelDefaultTarget));
      setChannelHitlTetherMode(Object.fromEntries(nextChannelHitlTetherMode));
    } catch (err) {
      setUsers([]);
      setAgents([]);
      setResult({ success: false, message: apiError(err) });
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void load();
  }, []);

  const hydrateUserDraft = (user: UserSettingsEntry) => {
    setIsAdmin((prev) => withRecordValue(prev, user.id, user.role === ROLE_ADMIN));
    setIsActive((prev) => withRecordValue(prev, user.id, user.isActive));
    setCanImportConnectors((prev) =>
      withRecordValue(prev, user.id, Boolean(user.canImportConnectors))
    );
    setCanImportSkills((prev) => withRecordValue(prev, user.id, Boolean(user.canImportSkills)));
    setChannelDeviceId((prev) => withRecordValue(prev, user.id, user.channelDeviceId ?? ''));
    setChannelBotToken((prev) => withRecordValue(prev, user.id, ''));
    if (user.channelDefaultTargetType === 'agent' && user.channelDefaultTargetId) {
      setChannelDefaultTarget((prev) =>
        withRecordValue(prev, user.id, `agent:${user.channelDefaultTargetId}`)
      );
    } else {
      setChannelDefaultTarget((prev) => withRecordValue(prev, user.id, ''));
    }
    setChannelHitlTetherMode((prev) => ({
      ...prev,
      ...Object.fromEntries((user.channelTetherModes ?? []).map((item) => [`${user.id}:${item.providerKey}`, item.hitlTetherMode])),
    }));
  };

  const getPersistedActiveAdminCount = (): number =>
    users.reduce((count, user) => count + (user.role === ROLE_ADMIN && user.isActive ? 1 : 0), 0);

  const getProjectedActiveAdminCount = (targetUser: UserSettingsEntry): number =>
    users.reduce((count, user) => {
      const nextIsAdmin =
        user.id === targetUser.id
          ? readRecordValue(isAdmin, user.id, false)
          : user.role === ROLE_ADMIN;
      const nextIsActive =
        user.id === targetUser.id
          ? readRecordValue(isActive, user.id, user.isActive)
          : user.isActive;

      return count + (nextIsAdmin && nextIsActive ? 1 : 0);
    }, 0);

  const getUserSaveBlockedReason = (user: UserSettingsEntry): string | null => {
    const persistedActiveAdminCount = getPersistedActiveAdminCount();
    const projectedActiveAdminCount = getProjectedActiveAdminCount(user);
    const isPersistedActiveAdmin = user.role === ROLE_ADMIN && user.isActive;

    if (
      isPersistedActiveAdmin &&
      persistedActiveAdminCount > 0 &&
      projectedActiveAdminCount === 0
    ) {
      return 'At least one active admin must remain in the system.';
    }

    return null;
  };

  const cancelUserEdits = (user: UserSettingsEntry) => {
    hydrateUserDraft(user);
    setEditingUserId(null);
  };

  const inviteUser = async (): Promise<boolean> => {
    const email = invite.email.trim();
    const username = invite.username.trim();
    const secret = invite.secret;
    if (!email && !secret.trim()) {
      setResult({
        success: false,
        message: 'Enter the email they use with Google or Microsoft, or set a password.',
      });
      return false;
    }

    if (secret.trim() && secret.trim().length < 8) {
      setResult({ success: false, message: 'Password must be at least 8 characters.' });
      return false;
    }

    if (
      username &&
      users.some((user) => normalizeUsername(user.username) === normalizeUsername(username))
    ) {
      setResult({ success: false, message: 'Username already exists.' });
      return false;
    }

    setResult(null);
    try {
      const invited = await settingsAdmin.inviteUser({
        username: username || null,
        email: email || null,
        password: secret.trim() ? secret : null,
        role: invite.role === 'elevated' ? ROLE_ADMIN : ROLE_MEMBER,
      });
      const inviteCode = (invited.data as { inviteCode?: string } | undefined)?.inviteCode;
      setInvite({ username: '', email: '', secret: '', role: 'standard' });
      setResult({
        success: true,
        message: inviteCode
          ? `Invited. Their one-time sign-in code is ${inviteCode}.`
          : 'Invited. They can sign in with their local password, or redeem the sign-in code with Google or Microsoft.',
      });
      await load();
      return true;
    } catch (err) {
      setResult({ success: false, message: apiError(err) });
      return false;
    }
  };

  const saveUserEdits = async (user: UserSettingsEntry) => {
    const blockedReason = getUserSaveBlockedReason(user);
    if (blockedReason) {
      setResult({ success: false, message: blockedReason });
      return;
    }

    setSavingUserId(user.id);
    setResult(null);
    try {
      await settingsAdmin.setUserRole(user.id, {
        isAdmin: readRecordValue(isAdmin, user.id, false),
      });
      await settingsAdmin.setUserActive(user.id, {
        isActive: readRecordValue(isActive, user.id, user.isActive),
      });
      await settingsAdmin.setUserImportPermissions(user.id, {
        canImportConnectors: readRecordValue(canImportConnectors, user.id, false),
        canImportSkills: readRecordValue(canImportSkills, user.id, false),
      });
      setResult({ success: true, message: 'User access settings saved.' });
      setEditingUserId(null);
      await load();
    } catch (err) {
      setResult({ success: false, message: apiError(err) });
    } finally {
      setSavingUserId(null);
    }
  };

  const saveChannelSettings = async (userId: string) => {
    setSavingUserId(userId);
    setResult(null);
    try {
      const selected = readRecordValue(channelDefaultTarget, userId, '').trim();
      const [defaultTargetType, ...targetParts] = selected.split(':');
      const defaultTargetId = targetParts.join(':');

      await settingsAdmin.saveUserChannelSettings(userId, 'telegram', {
        attachmentRole: 'chat',
        settings: {
          channel_device_id: readRecordValue(channelDeviceId, userId, '').trim(),
          bot_token: readRecordValue(channelBotToken, userId, '').trim() || null,
        },
        defaultTargetType: defaultTargetType || null,
        defaultTargetId: defaultTargetId || null,
        hitlTetherMode: readRecordValue(channelHitlTetherMode, `${userId}:telegram`, 'mirror'),
      });
      for (const tether of users.find((user) => user.id === userId)?.channelTetherModes ?? []) {
        if (tether.providerKey === 'telegram') continue;
        await settingsAdmin.saveUserChannelSettings(userId, tether.providerKey, {
          attachmentRole: 'chat', settings: {}, hitlTetherMode: readRecordValue(channelHitlTetherMode, `${userId}:${tether.providerKey}`, tether.hitlTetherMode),
        });
      }
      setChannelBotToken((prev) => withRecordValue(prev, userId, ''));
      setResult({ success: true, message: 'Telegram chat configured.' });
      await load();
    } catch (err) {
      setResult({ success: false, message: apiError(err) });
    } finally {
      setSavingUserId(null);
    }
  };

  const dispatchChannelHitl = async (userId: string) => {
    setSavingUserId(userId);
    setResult(null);
    try {
      const selected = readRecordValue(channelDefaultTarget, userId, '').trim();
      const [defaultTargetType, ...targetParts] = selected.split(':');
      const defaultTargetId = targetParts.join(':');

      const response = await settingsAdmin.dispatchUserChannelHitlTest(userId, 'telegram', {
        attachmentRole: 'chat',
        settings: {
          channel_device_id: readRecordValue(channelDeviceId, userId, '').trim(),
          bot_token: readRecordValue(channelBotToken, userId, '').trim() || null,
        },
        defaultTargetType: defaultTargetType || null,
        defaultTargetId: defaultTargetId || null,
        hitlTetherMode: readRecordValue(channelHitlTetherMode, `${userId}:telegram`, 'mirror'),
        prompt: 'Approve this action?',
        options: [
          { label: 'Approve', value: 'approve' },
          { label: 'Deny', value: 'deny' },
        ],
      });
      const interactionId = readApiString(response.data, 'interactionId') ?? 'pending';
      const runIdValue = readApiString(response.data, 'runId');
      const runId = runIdValue ? `, run ${runIdValue}` : '';
      setResult({ success: true, message: `Interaction dispatched (${interactionId}${runId}).` });
      await load();
    } catch (err) {
      setResult({ success: false, message: apiError(err) });
    } finally {
      setSavingUserId(null);
    }
  };

  const getIsAdminDraft = (userId: string): boolean => readRecordValue(isAdmin, userId, false);
  const getIsActiveDraft = (userId: string): boolean => readRecordValue(isActive, userId, true);
  const getCanImportConnectorsDraft = (userId: string): boolean =>
    readRecordValue(canImportConnectors, userId, false);
  const getCanImportSkillsDraft = (userId: string): boolean =>
    readRecordValue(canImportSkills, userId, false);
  const getChannelDeviceId = (userId: string): string =>
    readRecordValue(channelDeviceId, userId, '');
  const getChannelBotToken = (userId: string): string =>
    readRecordValue(channelBotToken, userId, '');
  const getChannelDefaultTarget = (userId: string): string =>
    readRecordValue(channelDefaultTarget, userId, '');
  const getChannelHitlTetherMode = (userId: string, providerKey: string): 'hitl' | 'mirror' =>
    readRecordValue(channelHitlTetherMode, `${userId}:${providerKey}`, 'mirror');
  const setIsAdminDraft = (userId: string, value: boolean) =>
    setIsAdmin((prev) => withRecordValue(prev, userId, value));
  const setIsActiveDraft = (userId: string, value: boolean) =>
    setIsActive((prev) => withRecordValue(prev, userId, value));
  const setCanImportConnectorsDraft = (userId: string, value: boolean) =>
    setCanImportConnectors((prev) => withRecordValue(prev, userId, value));
  const setCanImportSkillsDraft = (userId: string, value: boolean) =>
    setCanImportSkills((prev) => withRecordValue(prev, userId, value));
  const setChannelDeviceForUser = (userId: string, value: string) =>
    setChannelDeviceId((prev) => withRecordValue(prev, userId, value));
  const setChannelBotTokenForUser = (userId: string, value: string) =>
    setChannelBotToken((prev) => withRecordValue(prev, userId, value));
  const setChannelDefaultTargetForUser = (userId: string, value: string) =>
    setChannelDefaultTarget((prev) => withRecordValue(prev, userId, value));
  const setChannelHitlTetherModeForUser = (userId: string, providerKey: string, value: 'hitl' | 'mirror') =>
    setChannelHitlTetherMode((prev) => withRecordValue(prev, `${userId}:${providerKey}`, value));

  const filteredUsers = useMemo(() => {
    const q = search.trim().toLowerCase();
    return users.filter((user) => {
      const roleMatchesSelection =
        roleFilter === 'all' ||
        (roleFilter === 'elevated' && user.role === ROLE_ADMIN) ||
        (roleFilter === 'standard' && user.role !== ROLE_ADMIN);
      const statusMatch =
        statusFilter === 'all' ||
        (statusFilter === 'active' && user.isActive) ||
        (statusFilter === 'inactive' && !user.isActive);
      const searchMatch =
        !q ||
        user.username.toLowerCase().includes(q) ||
        user.id.toLowerCase().includes(q) ||
        String(user.email || '')
          .toLowerCase()
          .includes(q);
      return roleMatchesSelection && statusMatch && searchMatch;
    });
  }, [users, search, roleFilter, statusFilter]);

  const totalPages = Math.max(1, Math.ceil(filteredUsers.length / pageSize));
  const safePage = Math.min(page, totalPages);
  const startIndex = (safePage - 1) * pageSize;
  const pagedUsers = filteredUsers.slice(startIndex, startIndex + pageSize);

  useEffect(() => {
    setPage(1);
  }, [search, roleFilter, statusFilter, pageSize]);

  const statusItems = useMemo(() => {
    const activeUsersCount = users.filter((user) => user.isActive).length;
    const adminUsersCount = users.filter((user) => user.role === ROLE_ADMIN).length;
    const usersStatus = loading
      ? 'Checking identity directory...'
      : `${users.length} user account(s) • ${activeUsersCount} active`;
    const usersRecent =
      result?.message ??
      (users[0] ? `Latest user profile: ${users[0].username}` : 'No user changes yet');
    const usersSecurity = `${adminUsersCount} admin account(s) configured`;

    return [
      { title: 'Global Status', value: usersStatus },
      { title: 'Recent Activity', value: usersRecent },
      { title: 'Security', value: usersSecurity },
    ];
  }, [loading, users, result]);

  const clearExternalLogin = async (userId: string, provider: string) => {
    setSavingUserId(userId);
    setResult(null);
    try {
      await settingsAdmin.clearExternalLogin(userId, provider);
      await load();
      setResult({ success: true, message: 'Sign-in link removed.' });
    } catch (error) {
      setResult({ success: false, message: apiError(error) });
    } finally {
      setSavingUserId(null);
    }
  };

  return {
    users,
    agents,
    loading,
    savingUserId,
    editingUserId,
    result,
    setResult,
    search,
    roleFilter,
    statusFilter,
    page,
    pageSize,
    totalPages,
    safePage,
    pagedUsers,
    filteredUsers,
    invite,
    getIsAdminDraft,
    getIsActiveDraft,
    getCanImportConnectorsDraft,
    getCanImportSkillsDraft,
    getChannelDeviceId,
    getChannelBotToken,
    getChannelDefaultTarget,
    getChannelHitlTetherMode,
    getUserSaveBlockedReason,
    setSearch,
    setRoleFilter,
    setStatusFilter,
    setPage,
    setPageSize,
    setInvite,
    setIsAdminDraft,
    setIsActiveDraft,
    setCanImportConnectorsDraft,
    setCanImportSkillsDraft,
    setChannelDeviceForUser,
    setChannelBotTokenForUser,
    setChannelDefaultTargetForUser,
    setChannelHitlTetherModeForUser,
    setEditingUserId,
    inviteUser,
    saveUserEdits,
    hydrateUserDraft,
    cancelUserEdits,
    saveChannelSettings,
    dispatchChannelHitl,
    clearExternalLogin,
    statusItems,
  };
}

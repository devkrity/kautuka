/**
 * Auth API module
 * Handles token management, login, and logout
 */
import { apiClient } from '@/lib/apiClient';
import { getAccessToken, refreshAuthSession } from '@/lib/authState';
import {
  loginResponseSchema,
  refreshResponseSchema,
  userSchema,
  type LoginResponse,
  type RefreshResponse,
  type User,
} from '../schemas/authSchema';

function toRequestError(error: unknown, fallbackMessage: string): Error {
  if (error && typeof error === 'object' && 'response' in error) {
    const response = (error as { response?: { data?: { detail?: unknown; message?: unknown } } })
      .response;
    const detail = response?.data?.detail ?? response?.data?.message;
    if (typeof detail === 'string' && detail.length > 0) {
      return new Error(detail);
    }
  }

  if (error && typeof error === 'object' && 'detail' in error) {
    const detail = (error as { detail?: unknown }).detail;
    if (typeof detail === 'string' && detail.length > 0) {
      return new Error(detail);
    }
  }

  return new Error(fallbackMessage);
}

export interface ChannelProviderField {
  fieldKey: string;
  label: string;
  description?: string | null;
  fieldType: string;
  required: boolean;
  secret: boolean;
  defaultValue?: string | null;
  validationHint?: string | null;
  group?: string | null;
  enumValues: string[];
}

export interface ChannelProviderDefinition {
  providerKey: string;
  displayName: string;
  schemaVersion: string;
  capabilityVersion: string;
  fields: ChannelProviderField[];
}

export interface UserChannelSettings {
  providerKey: string;
  displayName: string;
  registrationId?: string | null;
  externalUserId?: string | null;
  defaultTargetType?: string | null;
  defaultTargetId?: string | null;
  hitlTetherMode?: 'hitl' | 'mirror' | null;
  settings: Record<string, unknown>;
  configuredSecretFieldKeys: string[];
  status?:
    | 'ACTIVE'
    | 'INACTIVE'
    | 'PROVISIONING'
    | 'ERROR'
    | 'SUSPENDED'
    | 'RECOVERY_REQUIRED'
    | 'DELETED';
}

export interface MyChannelSettingsResponse {
  user: User;
  providers: ChannelProviderDefinition[];
  channels: UserChannelSettings[];
  channelSetup?: {
    success: boolean;
    status: string;
    detail?: string | null;
  } | null;
}

export interface ChannelLifecycleResult {
  accepted: boolean;
  status: NonNullable<UserChannelSettings['status']>;
  detail: string;
}

export async function listExternalProviders(): Promise<{ google: boolean; microsoft: boolean }> {
  const res = await fetch('/api/auth/external/providers');
  if (!res.ok) {
    return { google: false, microsoft: false };
  }

  const body = (await res.json()) as { google?: boolean; microsoft?: boolean };
  return { google: body.google === true, microsoft: body.microsoft === true };
}

export async function startExternalLogin(
  provider: 'google' | 'microsoft',
  microsoftAccountType?: 'organizations' | 'consumers',
  intent: 'login' | 'link' = 'login'
): Promise<{ startUrl: string }> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  const accessToken = intent === 'link' ? getAccessToken() : null;
  if (accessToken) headers.Authorization = `Bearer ${accessToken}`;
  const res = await fetch('/api/auth/external/start', {
    method: 'POST',
    headers,
    body: JSON.stringify({ provider, microsoftAccountType, intent }),
  });
  if (!res.ok) {
    const body = (await res.json().catch(() => ({}))) as { detail?: string };
    throw new Error(body.detail ?? 'Sign-in is not configured.');
  }

  const result = (await res.json()) as { startUrl: string };
  if (intent === 'link') {
    const state = new URLSearchParams(new URL(result.startUrl).hash.slice(1)).get('state');
    if (!state) throw new Error('Sign-in was not accepted.');
    globalThis.sessionStorage.setItem(`kautukaExternalLoginIntent:${state}`, 'link');
  }
  return result;
}

export async function completeExternalLogin(
  code: string,
  state: string,
  brokerTicket: string
): Promise<LoginResponse> {
  const inviteCode = globalThis.sessionStorage.getItem('kautukaInviteCode');
  const setupToken = globalThis.sessionStorage.getItem('kautukaSetupToken');
  globalThis.sessionStorage.removeItem('kautukaInviteCode');
  globalThis.sessionStorage.removeItem('kautukaSetupToken');
  const res = await fetch('/api/auth/external/complete', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ code, state, brokerTicket, inviteCode, setupToken }),
  });
  if (!res.ok) {
    throw new Error('Sign-in was not accepted.');
  }

  return loginResponseSchema.parse(await res.json());
}

export async function completeExternalLink(
  code: string,
  state: string,
  brokerTicket: string
): Promise<LoginResponse> {
  const accessToken = getAccessToken();
  if (!accessToken) throw new Error('Sign-in was not accepted.');
  const res = await fetch('/api/auth/external/complete', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${accessToken}` },
    body: JSON.stringify({ code, state, brokerTicket }),
  });
  if (!res.ok) throw new Error('Sign-in was not accepted.');
  return loginResponseSchema.parse(await res.json());
}

export function externalLoginIntent(state: string): 'login' | 'link' {
  const key = `kautukaExternalLoginIntent:${state}`;
  const intent = globalThis.sessionStorage.getItem(key);
  globalThis.sessionStorage.removeItem(key);
  return intent === 'link' ? 'link' : 'login';
}

export async function clearMyExternalLogin(provider: string): Promise<void> {
  try {
    await apiClient.delete(`/settings/me/external-logins/${provider}`);
  } catch (error) {
    throw toRequestError(error, 'Failed to remove sign-in link.');
  }
}

export async function getSetupStatus(): Promise<{ needsSetup: boolean }> {
  const res = await fetch('/api/auth/setup/status');
  if (!res.ok) throw new Error('Failed to check setup status.');
  return res.json() as Promise<{ needsSetup: boolean }>;
}

export async function createFirstAdmin(
  username: string,
  password: string,
  setupToken: string
): Promise<void> {
  const res = await fetch('/api/auth/setup', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password, setupToken }),
  });
  if (res.status === 409) throw new Error('Setup has already been completed.');
  if (!res.ok) {
    const body = (await res.json().catch(() => ({}))) as { detail?: string };
    throw new Error(body.detail ?? 'Failed to create admin account.');
  }
}

export async function login(username: string, password: string): Promise<LoginResponse> {
  try {
    const response = await apiClient.post('/auth/login', { username, password });
    return loginResponseSchema.parse(response.data);
  } catch (error) {
    throw toRequestError(error, 'Login failed.');
  }
}

export async function logout(): Promise<void> {
  try {
    await apiClient.post('/auth/logout');
  } catch (error) {
    throw toRequestError(error, 'Logout failed.');
  }
}

/** Cookie-based silent refresh — no body, relies on httpOnly refresh cookie. */
export async function refreshViaCookie(): Promise<RefreshResponse> {
  const response = await refreshAuthSession();
  if (!response) throw new Error('Token refresh failed.');
  const parsed = refreshResponseSchema.parse(response);
  return parsed;
}

export async function getProfile(): Promise<User> {
  try {
    const response = await apiClient.get('/auth/me');
    return userSchema.parse(response.data);
  } catch (error) {
    throw toRequestError(error, 'Failed to load profile.');
  }
}

export async function updateMyProfile(body: {
  email?: string | null;
  displayName?: string | null;
}): Promise<User> {
  try {
    const response = await apiClient.put('/settings/me/profile', body);
    return userSchema.parse(response.data);
  } catch (error) {
    throw toRequestError(error, 'Failed to update profile.');
  }
}

export async function changeMyPassword(
  currentPassword: string,
  newPassword: string
): Promise<void> {
  try {
    await apiClient.put('/settings/me/password', { currentPassword, newPassword });
  } catch (error) {
    throw toRequestError(error, 'Failed to update password.');
  }
}

export async function getMyChannelSettings(): Promise<MyChannelSettingsResponse> {
  try {
    const response = await apiClient.get('/settings/me/channels');
    const payload = response.data as MyChannelSettingsResponse;
    payload.user = userSchema.parse(payload.user);
    return payload;
  } catch (error) {
    throw toRequestError(error, 'Failed to load channel settings.');
  }
}

export async function saveMyChannelSettings(
  providerKey: string,
  body: {
    settings: Record<string, unknown>;
    defaultTargetType?: string | null;
    defaultTargetId?: string | null;
    hitlTetherMode?: 'hitl' | 'mirror';
    attachmentRole?: 'agent' | 'workflows' | 'workflow';
    attachmentSubject?: string;
    clear?: boolean;
  }
): Promise<MyChannelSettingsResponse> {
  try {
    const response = await apiClient.put(`/settings/me/channels/${providerKey}`, body);
    const payload = response.data as MyChannelSettingsResponse;
    payload.user = userSchema.parse(payload.user);
    return payload;
  } catch (error) {
    throw toRequestError(error, 'Failed to save channel settings.');
  }
}

export async function sendMyChannelHitlTest(
  providerKey: string,
  body?: {
    prompt?: string;
    timeoutSeconds?: number;
    chatId?: string;
    settings?: Record<string, unknown>;
    defaultTargetType?: string | null;
    defaultTargetId?: string | null;
    hitlTetherMode?: 'hitl' | 'mirror';
  }
): Promise<void> {
  try {
    await apiClient.post(`/settings/me/channels/${providerKey}/hitl-test`, {
      prompt: body?.prompt ?? 'Approve this action?',
      timeoutSeconds: body?.timeoutSeconds ?? 900,
      chatId: body?.chatId ?? '',
      settings: body?.settings ?? {},
      defaultTargetType: body?.defaultTargetType ?? null,
      defaultTargetId: body?.defaultTargetId ?? null,
      hitlTetherMode: body?.hitlTetherMode ?? 'mirror',
    });
  } catch (error) {
    throw toRequestError(error, 'Failed to send channel test.');
  }
}

export async function startMySlackPairing(): Promise<{ code: string }> {
  try {
    const response = await apiClient.post('/settings/me/channels/slack/pairing');
    return response.data as { code: string };
  } catch (error) {
    throw toRequestError(error, 'Could not start Slack account linking.');
  }
}

export async function getMySlackPairingStatus(): Promise<{
  paired: boolean;
  reason?: string | null;
}> {
  try {
    const response = await apiClient.get('/settings/me/channels/slack/pairing');
    return response.data as { paired: boolean; reason?: string | null };
  } catch (error) {
    throw toRequestError(error, 'Could not check Slack account linking.');
  }
}

export async function sendMySlackTestMessage(): Promise<void> {
  try {
    await apiClient.post('/settings/me/channels/slack/test-message');
  } catch (error) {
    throw toRequestError(error, 'Could not send the Slack test message.');
  }
}

export async function getMySlackAppCreationUrl(): Promise<string> {
  try {
    const response = await apiClient.get('/settings/channels/slack/create-app', {
      responseType: 'text',
    });
    const url = response.data as string;
    if (!url.startsWith('https://api.slack.com/apps?new_app=1&manifest_json=')) {
      throw new Error('Kautuka returned an invalid Slack app setup URL.');
    }
    return url;
  } catch (error) {
    throw toRequestError(error, 'Could not create the Slack app setup link.');
  }
}

export async function transitionMyChannelLifecycle(
  providerKey: string,
  transition: 'suspend' | 'recover' | 'remove'
): Promise<ChannelLifecycleResult> {
  try {
    const response = await apiClient.post(`/settings/me/channels/${providerKey}/lifecycle`, {
      transition,
      reason: 'user_requested',
    });
    return response.data as ChannelLifecycleResult;
  } catch (error) {
    throw toRequestError(error, 'Failed to update channel status.');
  }
}

export {
  setAccessToken,
  getAccessToken,
  setAuthFailureHandler,
  refreshAuthSession,
  reportAuthFailure,
} from '@/lib/authState';

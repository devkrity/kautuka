import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  completeExternalLink,
  completeExternalLogin,
  externalLoginIntent,
  startExternalLogin,
} from '@/features/auth/api/auth';

const { getAccessTokenMock } = vi.hoisted(() => ({ getAccessTokenMock: vi.fn() }));
vi.mock('@/lib/authState', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/authState')>();
  return { ...actual, getAccessToken: getAccessTokenMock };
});

describe('external login API', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    sessionStorage.clear();
    getAccessTokenMock.mockReset();
  });

  it('completes login with the broker ticket and never forwards a bearer token', async () => {
    getAccessTokenMock.mockReturnValue('victim-access-token');
    const fetchMock = vi
      .fn()
      .mockResolvedValue(
        new Response(
          JSON.stringify({
            accessToken: 'new',
            tokenType: 'bearer',
            expiresInSeconds: 60,
            user: {},
          }),
          { status: 200 }
        )
      );
    vi.stubGlobal('fetch', fetchMock);
    await completeExternalLogin('code', 'state', 'ticket');
    const [, request] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(new Headers(request.headers).has('Authorization')).toBe(false);
    expect(JSON.parse(String(request.body))).toEqual({
      code: 'code',
      state: 'state',
      brokerTicket: 'ticket',
      inviteCode: null,
      setupToken: null,
    });
  });

  it('sends bearer authentication only for explicit link intent and omits invitation/setup credentials', async () => {
    getAccessTokenMock.mockReturnValue('owner-access-token');
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ startUrl: 'https://auth.example/start#state=state-1' }), {
          status: 200,
        })
      )
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            accessToken: 'new',
            tokenType: 'bearer',
            expiresInSeconds: 60,
            user: {},
          }),
          { status: 200 }
        )
      );
    vi.stubGlobal('fetch', fetchMock);
    await startExternalLogin('google', undefined, 'link');
    const [, startRequest] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(new Headers(startRequest.headers).get('Authorization')).toBe(
      'Bearer owner-access-token'
    );
    expect(externalLoginIntent('state-1')).toBe('link');
    await completeExternalLink('code', 'state-1', 'ticket');
    const [, completeRequest] = fetchMock.mock.calls[1] as [string, RequestInit];
    expect(new Headers(completeRequest.headers).get('Authorization')).toBe(
      'Bearer owner-access-token'
    );
    expect(JSON.parse(String(completeRequest.body))).toEqual({
      code: 'code',
      state: 'state-1',
      brokerTicket: 'ticket',
    });
  });
});

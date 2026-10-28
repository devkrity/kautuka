import { afterEach, describe, expect, it, vi } from 'vitest';

function refreshResponse(accessToken: string, expiresInSeconds = 900): Response {
  return new Response(JSON.stringify({ accessToken, tokenType: 'bearer', expiresInSeconds }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

describe('authenticated fetch coordination', () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.resetModules();
  });

  it('coalesces concurrent refresh requests into one cookie refresh', async () => {
    const fetchMock = vi.fn().mockResolvedValue(refreshResponse('fresh-token'));
    vi.stubGlobal('fetch', fetchMock);
    const { refreshAuthSession } = await import('@/lib/authState');

    const [first, second] = await Promise.all([refreshAuthSession(), refreshAuthSession()]);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(first?.accessToken).toBe('fresh-token');
    expect(second?.accessToken).toBe('fresh-token');
  });

  it('refreshes the in-memory access session before it expires', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-07-15T00:00:00Z'));
    const fetchMock = vi.fn().mockResolvedValue(refreshResponse('fresh-token'));
    vi.stubGlobal('fetch', fetchMock);
    const authState = await import('@/lib/authState');
    authState.setAccessToken('initial-token', Date.now() + 61_000);

    await vi.advanceTimersByTimeAsync(1_000);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/auth/refresh',
      expect.objectContaining({ method: 'POST', credentials: 'include' })
    );
    expect(authState.getAccessToken()).toBe('fresh-token');
    authState.setAccessToken(null);
  });

  it('refreshes and retries a protected request once with the rotated token', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response(null, { status: 401 }))
      .mockResolvedValueOnce(refreshResponse('fresh-token'))
      .mockResolvedValueOnce(new Response('ok', { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);
    const authState = await import('@/lib/authState');
    const { authenticatedFetch } = await import('@/lib/authenticatedFetch');
    authState.setAccessToken('expired-token', Date.now() + 10 * 60_000);

    const response = await authenticatedFetch('/api/protected');

    expect(response.status).toBe(200);
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(fetchMock.mock.calls[1]?.[0]).toBe('/api/auth/refresh');
    const initialHeaders = new Headers((fetchMock.mock.calls[0]?.[1] as RequestInit).headers);
    const retryHeaders = new Headers((fetchMock.mock.calls[2]?.[1] as RequestInit).headers);
    expect(initialHeaders.get('Authorization')).toBe('Bearer expired-token');
    expect(retryHeaders.get('Authorization')).toBe('Bearer fresh-token');
  });

  it('reports one auth failure when concurrent requests cannot refresh', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) =>
      String(input) === '/api/auth/refresh'
        ? new Response(null, { status: 401 })
        : new Response(null, { status: 401 })
    );
    vi.stubGlobal('fetch', fetchMock);
    const authState = await import('@/lib/authState');
    const { authenticatedFetch } = await import('@/lib/authenticatedFetch');
    const failureHandler = vi.fn();
    authState.setAccessToken('expired-token', Date.now() + 10 * 60_000);
    authState.setAuthFailureHandler(failureHandler);

    await Promise.all([
      authenticatedFetch('/api/protected/one'),
      authenticatedFetch('/api/protected/two'),
    ]);

    expect(fetchMock.mock.calls.filter(([input]) => input === '/api/auth/refresh')).toHaveLength(1);
    expect(failureHandler).toHaveBeenCalledTimes(1);
  });
});

import { afterEach, describe, expect, it, vi } from 'vitest';
import { waitFor } from '@testing-library/react';
import { runBoundExternalLogin, runBoundMailLogin } from '@/features/auth/api/boundExternalLogin';
import { setAccessToken } from '@/lib/authState';

const token = 'A'.repeat(43);

describe('bound external login', () => {
  afterEach(() => { setAccessToken(null); vi.restoreAllMocks(); vi.unstubAllGlobals(); sessionStorage.clear(); });

  it('explains broker suspension without starting or retrying mail sign-in', async () => {
    setAccessToken('local-access', Date.now() + 3600000);
    const popup = { location: { replace: vi.fn() }, close: vi.fn() } as unknown as Window;
    vi.spyOn(window, 'open').mockReturnValue(popup);
    const fetchMock = vi.fn(async () => new Response(null, { status: 403,
      headers: { 'Kautuka-Installation-Error': 'installation_suspended' } }));
    vi.stubGlobal('fetch', fetchMock);
    await expect(runBoundMailLogin('kautuka-m365-mail')).rejects.toThrow('broker operator suspended this installation');
    expect(fetchMock).toHaveBeenCalledOnce();
    expect(popup.close).toHaveBeenCalledOnce();
  });

  it('uses authenticated mail endpoints and validates the popup binding', async () => {
    setAccessToken('local-access', Date.now() + 3600000);
    const replace = vi.fn();
    const popup = { location: { replace }, close: vi.fn() } as unknown as Window;
    vi.spyOn(window, 'open').mockReturnValue(popup);
    const calls: string[] = [];
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input); calls.push(path);
      expect((init?.headers as Record<string, string>).Authorization).toBe('Bearer local-access');
      if (path.endsWith('bound-begin')) return Response.json({ startUrl: `https://auth.example/handoff#id=${token}&flow=${token}`,
        brokerOrigin: 'https://auth.example', state: token, flowId: token, tabHandle: token });
      if (path.endsWith('bound-bind')) return new Response(null, { status: 204 });
      if (path.endsWith('bound-complete')) return Response.json({ status: 'connected', message: 'Signed in.' });
      throw new Error('unexpected request');
    }));
    const operation = runBoundMailLogin('bundle::gmail');
    await waitFor(() => expect(replace).toHaveBeenCalledOnce());
    window.dispatchEvent(new MessageEvent('message', { origin: 'https://wrong.example', source: popup,
      data: { type: 'kautuka-broker-candidate', flowId: token, nonce: token } }));
    expect(calls).toHaveLength(1);
    window.dispatchEvent(new MessageEvent('message', { origin: 'https://auth.example', source: popup,
      data: { type: 'kautuka-broker-candidate', flowId: token, nonce: token } }));
    await operation;
    expect(calls).toEqual(['/api/connectors/bundle%3A%3Agmail/oauth/bound-begin', '/api/connectors/oauth/bound-bind', '/api/connectors/oauth/bound-complete']);
  });

  it('accepts only a candidate from the expected broker popup and flow', async () => {
    const replace = vi.fn();
    const close = vi.fn();
    const popup = { location: { replace }, close } as unknown as Window;
    vi.spyOn(window, 'open').mockReturnValue(popup);
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input);
      if (path.endsWith('bound-start')) return Response.json({
        startUrl: `https://auth.example/handoff#id=${token}&flow=${token}`,
        brokerOrigin: 'https://auth.example', state: token, flowId: token, tabHandle: token,
      });
      if (path.endsWith('bind-browser')) return new Response(null, { status: 204 });
      if (path.endsWith('bound-complete')) return Response.json({ accessToken: 'access', tokenType: 'bearer', expiresInSeconds: 900, user: {} });
      throw new Error('unexpected request');
    });
    vi.stubGlobal('fetch', fetchMock);
    const operation = runBoundExternalLogin('google', undefined, 'login', { setupToken: 'setup' });
    await waitFor(() => expect(replace).toHaveBeenCalledOnce());
    const message = { type: 'kautuka-broker-candidate', flowId: token, nonce: token };
    window.dispatchEvent(new MessageEvent('message', { origin: 'https://attacker.example', source: popup, data: message }));
    window.dispatchEvent(new MessageEvent('message', { origin: 'https://auth.example', source: window, data: message }));
    window.dispatchEvent(new MessageEvent('message', { origin: 'https://auth.example', source: popup, data: { ...message, flowId: 'B'.repeat(43) } }));
    expect(fetchMock).toHaveBeenCalledTimes(1);
    window.dispatchEvent(new MessageEvent('message', { origin: 'https://auth.example', source: popup, data: message }));
    expect((await operation).accessToken).toBe('access');
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(close).toHaveBeenCalledOnce();
  });

  it('fails cleanly when the browser blocks the popup', async () => {
    vi.spyOn(window, 'open').mockReturnValue(null);
    await expect(runBoundExternalLogin('google')).rejects.toThrow('Allow popups');
  });

  it('requests server cancellation when a bound mail attempt is aborted', async () => {
    setAccessToken('local-access', Date.now() + 3600000);
    const replace = vi.fn();
    const popup = { location: { replace }, close: vi.fn() } as unknown as Window;
    vi.spyOn(window, 'open').mockReturnValue(popup);
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).endsWith('bound-begin')) return Response.json({ startUrl: `https://auth.example/handoff#id=${token}&flow=${token}`,
        brokerOrigin: 'https://auth.example', state: token, flowId: token, tabHandle: token });
      if (String(input).endsWith('bound-cancel')) return new Response(null, { status: 204 });
      throw new Error('unexpected request');
    });
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();
    const operation = runBoundMailLogin('gmail', controller.signal);
    await waitFor(() => expect(replace).toHaveBeenCalledOnce());
    controller.abort();
    await expect(operation).rejects.toMatchObject({ name: 'AbortError' });
    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/api/connectors/oauth/bound-cancel', expect.objectContaining({ method: 'POST', keepalive: true })));
  });

  it('stops polling when a new-account quota requires a fresh sign-in', async () => {
    setAccessToken('local-access', Date.now() + 3600000);
    const popup = { location: { replace: vi.fn() }, close: vi.fn() } as unknown as Window;
    vi.spyOn(window, 'open').mockReturnValue(popup);
    let completions = 0;
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input);
      if (path.endsWith('bound-begin')) return Response.json({ startUrl: 'https://auth.example/handoff',
        brokerOrigin: 'https://auth.example', state: token, flowId: token, tabHandle: token });
      if (path.endsWith('bound-bind') || path.endsWith('bound-cancel')) return new Response(null, { status: 204 });
      if (path.endsWith('bound-complete')) { completions++; return new Response(null, { status: 429,
        headers: { 'Kautuka-Admission-Denied': 'new_account_limit', 'Retry-After': '600' } }); }
      throw new Error('unexpected request');
    }));
    const operation = runBoundMailLogin('gmail');
    await waitFor(() => expect(popup.location.replace).toHaveBeenCalledOnce());
    window.dispatchEvent(new MessageEvent('message', { origin: 'https://auth.example', source: popup,
      data: { type: 'kautuka-broker-candidate', flowId: token, nonce: token } }));
    await expect(operation).rejects.toThrow('reached its new mail account limit');
    expect(completions).toBe(1);
  });

  it('tells the user to disconnect a mailbox when five accounts are already active', async () => {
    setAccessToken('local-access', Date.now() + 3600000);
    const popup = { location: { replace: vi.fn() }, close: vi.fn() } as unknown as Window;
    vi.spyOn(window, 'open').mockReturnValue(popup);
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input);
      if (path.endsWith('bound-begin')) return Response.json({ startUrl: 'https://auth.example/handoff',
        brokerOrigin: 'https://auth.example', state: token, flowId: token, tabHandle: token });
      if (path.endsWith('bound-bind') || path.endsWith('bound-cancel')) return new Response(null, { status: 204 });
      if (path.endsWith('bound-complete')) return new Response(null, { status: 409,
        headers: { 'Kautuka-Admission-Denied': 'active_account_limit' } });
      throw new Error('unexpected request');
    }));
    const operation = runBoundMailLogin('gmail');
    await waitFor(() => expect(popup.location.replace).toHaveBeenCalledOnce());
    window.dispatchEvent(new MessageEvent('message', { origin: 'https://auth.example', source: popup,
      data: { type: 'kautuka-broker-candidate', flowId: token, nonce: token } }));
    await expect(operation).rejects.toThrow('Disconnect one before connecting another');
  });

  it('explains when the provider did not grant required mailbox permissions', async () => {
    setAccessToken('local-access', Date.now() + 3600000);
    const popup = { location: { replace: vi.fn() }, close: vi.fn() } as unknown as Window;
    vi.spyOn(window, 'open').mockReturnValue(popup);
    let completions = 0;
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input);
      if (path.endsWith('bound-begin')) return Response.json({ startUrl: 'https://auth.example/handoff',
        brokerOrigin: 'https://auth.example', state: token, flowId: token, tabHandle: token });
      if (path.endsWith('bound-bind') || path.endsWith('bound-cancel')) return new Response(null, { status: 204 });
      if (path.endsWith('bound-complete')) { completions++; return new Response(null, { status: 403,
        headers: { 'Kautuka-Mail-Error': 'insufficient_scope' } }); }
      throw new Error('unexpected request');
    }));
    const operation = runBoundMailLogin('gmail');
    await waitFor(() => expect(popup.location.replace).toHaveBeenCalledOnce());
    window.dispatchEvent(new MessageEvent('message', { origin: 'https://auth.example', source: popup,
      data: { type: 'kautuka-broker-candidate', flowId: token, nonce: token } }));
    await expect(operation).rejects.toThrow('Grant the requested mail permissions');
    expect(completions).toBe(1);
  });

  it('retries a lost binding and completion response using the same flow', async () => {
    const replace = vi.fn();
    const popup = { location: { replace }, close: vi.fn() } as unknown as Window;
    vi.spyOn(window, 'open').mockReturnValue(popup);
    let binds = 0;
    let completions = 0;
    const completeBodies: string[] = [];
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input);
      if (path.endsWith('bound-start')) return Response.json({
        startUrl: `https://auth.example/handoff#id=${token}&flow=${token}`,
        brokerOrigin: 'https://auth.example', state: token, flowId: token, tabHandle: token,
      });
      if (path.endsWith('bind-browser')) {
        if (++binds === 1) throw new TypeError('response lost');
        return new Response(null, { status: 204 });
      }
      if (path.endsWith('bound-complete')) {
        completeBodies.push(String(init?.body));
        if (++completions === 1) throw new TypeError('response lost after completion');
        return Response.json({ accessToken: 'access', tokenType: 'bearer', expiresInSeconds: 900, user: {} });
      }
      throw new Error('unexpected request');
    }));
    const operation = runBoundExternalLogin('google', undefined, 'login', { setupToken: 'setup' });
    await waitFor(() => expect(replace).toHaveBeenCalledOnce());
    window.dispatchEvent(new MessageEvent('message', { origin: 'https://auth.example', source: popup,
      data: { type: 'kautuka-broker-candidate', flowId: token, nonce: token } }));
    expect((await operation).accessToken).toBe('access');
    expect(binds).toBe(2);
    expect(completeBodies).toHaveLength(2);
    expect(completeBodies[1]).toBe(completeBodies[0]);
  });

  it('discards a late successful response after cancellation', async () => {
    const replace = vi.fn();
    const popup = { location: { replace }, close: vi.fn() } as unknown as Window;
    vi.spyOn(window, 'open').mockReturnValue(popup);
    let complete: ((response: Response) => void) | undefined;
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).endsWith('bound-start')) return Response.json({
        startUrl: `https://auth.example/handoff#id=${token}&flow=${token}`,
        brokerOrigin: 'https://auth.example', state: token, flowId: token, tabHandle: token,
      });
      if (String(input).endsWith('bind-browser')) return new Response(null, { status: 204 });
      return new Promise<Response>((resolve) => { complete = resolve; });
    }));
    const controller = new AbortController();
    const operation = runBoundExternalLogin('google', undefined, 'login', {}, controller.signal);
    await waitFor(() => expect(replace).toHaveBeenCalledOnce());
    window.dispatchEvent(new MessageEvent('message', { origin: 'https://auth.example', source: popup,
      data: { type: 'kautuka-broker-candidate', flowId: token, nonce: token } }));
    await waitFor(() => expect(complete).toBeDefined());
    controller.abort();
    complete!(Response.json({ accessToken: 'late', tokenType: 'bearer', expiresInSeconds: 900, user: {} }));
    await expect(operation).rejects.toMatchObject({ name: 'AbortError' });
    expect(popup.close).toHaveBeenCalledOnce();
  });
});

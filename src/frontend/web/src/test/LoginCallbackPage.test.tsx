import React from 'react';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { captureLoginCallbackHash } from '@/pages/loginCallbackHash';
import { LoginCallbackPage } from '@/pages/LoginCallbackPage';

const { completeExternalLoginMock, completeExternalLinkMock, setAccessTokenMock } = vi.hoisted(
  () => ({
    completeExternalLoginMock: vi.fn(),
    completeExternalLinkMock: vi.fn(),
    setAccessTokenMock: vi.fn(),
  })
);

vi.mock('@/features/auth/api/auth', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/features/auth/api/auth')>();
  return {
    ...actual,
    completeExternalLogin: completeExternalLoginMock,
    completeExternalLink: completeExternalLinkMock,
    setAccessToken: setAccessTokenMock,
  };
});

describe('LoginCallbackPage', () => {
  afterEach(() => {
    cleanup();
    completeExternalLoginMock.mockReset();
    completeExternalLinkMock.mockReset();
    setAccessTokenMock.mockReset();
    sessionStorage.clear();
  });

  it('strips the callback hash before the page posts it', () => {
    window.history.replaceState(
      null,
      '',
      '/login/callback#error=access_denied&error_description=secret'
    );
    captureLoginCallbackHash();
    expect(window.location.hash).toBe('');
    expect(window.location.pathname).toBe('/login/callback');
    expect(sessionStorage.getItem('kautukaLoginCallbackHash')).toContain(
      'error_description=secret'
    );
  });

  it('shows the fixed message for a provider error and does not post the description', async () => {
    sessionStorage.setItem(
      'kautukaLoginCallbackHash',
      '#error=access_denied&error_description=secret-provider-text&state=abc'
    );
    render(
      <MemoryRouter>
        <LoginCallbackPage onLoginSuccess={vi.fn()} />
      </MemoryRouter>
    );

    expect(await screen.findByText('Sign-in was not accepted.')).toBeInTheDocument();
    expect(screen.queryByText(/secret-provider-text/)).not.toBeInTheDocument();
    expect(completeExternalLoginMock).not.toHaveBeenCalled();
    expect(sessionStorage.getItem('kautukaLoginCallbackHash')).toBeNull();
  });

  it('stores the access token after a successful code exchange', async () => {
    const onLoginSuccess = vi.fn();
    sessionStorage.setItem('kautukaLoginCallbackHash', '#code=abc&state=xyz&broker_ticket=ticket');
    sessionStorage.setItem('kautukaExternalLoginNext', '/dashboard');
    completeExternalLoginMock.mockResolvedValue({
      accessToken: 'access-token',
      expiresInSeconds: 60,
    });

    render(
      <MemoryRouter>
        <LoginCallbackPage onLoginSuccess={onLoginSuccess} />
      </MemoryRouter>
    );

    await waitFor(() =>
      expect(completeExternalLoginMock).toHaveBeenCalledWith('abc', 'xyz', 'ticket')
    );
    expect(setAccessTokenMock).toHaveBeenCalledWith('access-token', expect.any(Number));
    expect(onLoginSuccess).toHaveBeenCalledOnce();
  });

  it('uses authenticated link completion only for a locally recorded link flow', async () => {
    sessionStorage.setItem('kautukaLoginCallbackHash', '#code=abc&state=xyz&broker_ticket=ticket');
    sessionStorage.setItem('kautukaExternalLoginIntent:xyz', 'link');
    completeExternalLinkMock.mockResolvedValue({
      accessToken: 'access-token',
      expiresInSeconds: 60,
    });
    render(
      <MemoryRouter>
        <LoginCallbackPage onLoginSuccess={vi.fn()} />
      </MemoryRouter>
    );
    await waitFor(() =>
      expect(completeExternalLinkMock).toHaveBeenCalledWith('abc', 'xyz', 'ticket')
    );
    expect(completeExternalLoginMock).not.toHaveBeenCalled();
    expect(sessionStorage.getItem('kautukaExternalLoginIntent:xyz')).toBeNull();
  });

  it('rejects duplicate broker tickets instead of choosing one', async () => {
    sessionStorage.setItem(
      'kautukaLoginCallbackHash',
      '#code=abc&state=xyz&broker_ticket=one&broker_ticket=two'
    );
    render(
      <MemoryRouter>
        <LoginCallbackPage onLoginSuccess={vi.fn()} />
      </MemoryRouter>
    );
    expect(await screen.findByText('Sign-in was not accepted.')).toBeInTheDocument();
    expect(completeExternalLoginMock).not.toHaveBeenCalled();
  });
});

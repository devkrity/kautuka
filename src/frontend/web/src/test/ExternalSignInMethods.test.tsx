import React from 'react';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { listExternalProvidersMock, runBoundExternalLoginMock, setAccessTokenMock } = vi.hoisted(() => ({
  listExternalProvidersMock: vi.fn(),
  runBoundExternalLoginMock: vi.fn(),
  setAccessTokenMock: vi.fn(),
}));

vi.mock('@/features/auth/api/auth', () => ({
  listExternalProviders: listExternalProvidersMock,
  setAccessToken: setAccessTokenMock,
}));
vi.mock('@/features/auth/api/boundExternalLogin', () => ({ runBoundExternalLogin: runBoundExternalLoginMock }));
vi.mock('@/lib/queryClient', () => ({ queryClient: { invalidateQueries: vi.fn() } }));

import { ExternalSignInMethods } from '@/features/auth/components/ExternalSignInMethods';

describe('ExternalSignInMethods', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    listExternalProvidersMock.mockResolvedValue({ google: true, microsoft: true });
    runBoundExternalLoginMock.mockResolvedValue({ accessToken: 'token', expiresInSeconds: 900 });
  });

  afterEach(() => cleanup());

  it('starts an authenticated bound link flow and updates the local token', async () => {
    render(<ExternalSignInMethods linkedProviders={[]} onError={vi.fn()} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Link Google account' }));

    await waitFor(() => {
      expect(runBoundExternalLoginMock).toHaveBeenCalledWith('google', undefined, 'link', {}, expect.any(AbortSignal));
      expect(setAccessTokenMock).toHaveBeenCalledWith('token', expect.any(Number));
    });
  });

  it('offers both Microsoft account types while hiding providers already linked', async () => {
    render(<ExternalSignInMethods linkedProviders={['google']} onError={vi.fn()} />);

    expect(await screen.findByRole('button', { name: 'Link work or school account' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Link personal Microsoft account' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Link Google account' })).not.toBeInTheDocument();
  });

  it('does not apply a late result after the page unmounts', async () => {
    let finish!: (value: { accessToken: string; expiresInSeconds: number }) => void;
    runBoundExternalLoginMock.mockReturnValue(new Promise((resolve) => { finish = resolve; }));
    const view = render(<ExternalSignInMethods linkedProviders={[]} onError={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Link Google account' }));
    const signal = runBoundExternalLoginMock.mock.calls[0][4] as AbortSignal;
    view.unmount();
    expect(signal.aborted).toBe(true);
    finish({ accessToken: 'late', expiresInSeconds: 900 });
    await Promise.resolve();
    expect(setAccessTokenMock).not.toHaveBeenCalled();
  });
});

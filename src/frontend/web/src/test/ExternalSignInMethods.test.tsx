import React from 'react';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { listExternalProvidersMock, startExternalLoginMock } = vi.hoisted(() => ({
  listExternalProvidersMock: vi.fn(),
  startExternalLoginMock: vi.fn(),
}));

vi.mock('@/features/auth/api/auth', () => ({
  listExternalProviders: listExternalProvidersMock,
  startExternalLogin: startExternalLoginMock,
}));

import { ExternalSignInMethods } from '@/features/auth/components/ExternalSignInMethods';

describe('ExternalSignInMethods', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    listExternalProvidersMock.mockResolvedValue({ google: true, microsoft: true });
    startExternalLoginMock.mockResolvedValue({ startUrl: 'https://auth.example/start#state=abc' });
  });

  afterEach(() => cleanup());

  it('starts an explicit authenticated link flow and redirects to its confirmation page', async () => {
    const onRedirect = vi.fn();
    render(<ExternalSignInMethods linkedProviders={[]} onError={vi.fn()} onRedirect={onRedirect} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Link Google account' }));

    await waitFor(() => {
      expect(startExternalLoginMock).toHaveBeenCalledWith('google', undefined, 'link');
      expect(onRedirect).toHaveBeenCalledWith('https://auth.example/start#state=abc');
    });
  });

  it('offers both Microsoft account types while hiding providers already linked', async () => {
    render(<ExternalSignInMethods linkedProviders={['google']} onError={vi.fn()} onRedirect={vi.fn()} />);

    expect(await screen.findByRole('button', { name: 'Link work or school account' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Link personal Microsoft account' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Link Google account' })).not.toBeInTheDocument();
  });
});

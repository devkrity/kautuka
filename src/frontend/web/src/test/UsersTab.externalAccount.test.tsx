import React from 'react';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi, beforeEach, describe, expect, it } from 'vitest';

const { settingsAdmin } = vi.hoisted(() => ({
  settingsAdmin: {
    listUsers: vi.fn(),
    listAgents: vi.fn(),
    inviteUser: vi.fn(),
    setUserRole: vi.fn(),
    setUserActive: vi.fn(),
    setUserImportPermissions: vi.fn(),
  },
}));

vi.mock('@/features/settings/api/settingsAdmin', () => ({ settingsAdmin }));

import { UsersTab } from '@/features/settings/components/UsersTab';

const socialAccount = {
  id: 'google-user-1',
  username: 'google.user',
  email: 'google.user@example.com',
  displayName: 'Google User',
  role: 'user',
  isActive: true,
  canImportConnectors: false,
  canImportSkills: false,
  externalProviders: ['google', 'microsoft'],
  channelTetherModes: [{ registrationId: 'telegram-default', providerKey: 'telegram', hitlTetherMode: 'mirror' as const }],
};

describe('UsersTab external account editing', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    settingsAdmin.listUsers.mockResolvedValue({ data: { users: [socialAccount] } });
    settingsAdmin.listAgents.mockResolvedValue({ data: { agents: [] } });
    settingsAdmin.setUserRole.mockResolvedValue({ data: { success: true } });
    settingsAdmin.setUserActive.mockResolvedValue({ data: { success: true } });
    settingsAdmin.setUserImportPermissions.mockResolvedValue({ data: { success: true } });
  });

  it('opens and saves an externally linked account without changing the list role filter', async () => {
    render(<UsersTab />);

    await screen.findByText('google.user');
    const roleFilter = screen.getByTitle('Filter role') as HTMLSelectElement;
    expect(roleFilter.value).toBe('all');
    const search = screen.getByRole('searchbox', { name: '' }) as HTMLInputElement;
    expect(search.name).toBe('user-directory-search');
    expect(search.autocomplete).toBe('off');

    fireEvent.click(screen.getByRole('button', { name: 'Edit' }));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('google.user')).toBeInTheDocument();
    expect(roleFilter.value).toBe('all');
    const botToken = within(dialog).getByLabelText('Bot Token') as HTMLInputElement;
    expect(botToken.value).toBe('');
    expect(botToken.autocomplete).toBe('new-password');

    fireEvent.click(within(dialog).getByRole('button', { name: 'Save access changes' }));

    await waitFor(() => {
      expect(settingsAdmin.setUserRole).toHaveBeenCalledWith('google-user-1', { isAdmin: false });
      expect(settingsAdmin.setUserActive).toHaveBeenCalledWith('google-user-1', { isActive: true });
      expect(settingsAdmin.setUserImportPermissions).toHaveBeenCalledWith('google-user-1', {
        canImportConnectors: false,
        canImportSkills: false,
      });
    });
    expect(roleFilter.value).toBe('all');
  });

  it('defaults a new channel draft to mirror mode', async () => {
    render(<UsersTab />);

    await screen.findByText('google.user');
    fireEvent.click(screen.getByRole('button', { name: 'Edit' }));

    const dialog = await screen.findByRole('dialog');
    const channelSync = within(dialog).getByTitle('telegram channel sync mode') as HTMLSelectElement;
    expect(channelSync.value).toBe('mirror');
  });

  it('keeps the new invitation code in the dialog until the administrator closes it', async () => {
    const user = userEvent.setup();
    settingsAdmin.inviteUser.mockResolvedValue({ data: { inviteCode: 'example-invite-code' } });
    render(<UsersTab />);
    await screen.findByText('google.user');
    fireEvent.click(screen.getByRole('button', { name: 'Invite user' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('Email'), { target: { value: 'new.user@example.com' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Invite' }));
    const code = await within(dialog).findByRole('textbox', { name: 'One-time invitation code' });
    expect(code).toHaveValue('example-invite-code');
    await user.click(within(dialog).getByRole('button', { name: 'Copy invitation' }));
    const invitation = await navigator.clipboard.readText();
    const joinLink = new URL(invitation.split('\n')[1]);
    expect(joinLink.href).toBe(`${window.location.origin}/login?join=1`);
    expect(invitation).toContain('On the computer running Kautuka');
    expect(invitation).toContain('example-invite-code');
    expect(invitation).toContain('Google or Microsoft using new.user@example.com');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Done' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(screen.queryByDisplayValue('example-invite-code')).not.toBeInTheDocument();
  });

  it('keeps the password-only success message after refreshing the directory', async () => {
    settingsAdmin.inviteUser.mockResolvedValue({ data: { inviteCode: null } });
    render(<UsersTab />);
    await screen.findByText('google.user');
    fireEvent.click(screen.getByRole('button', { name: 'Invite user' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('Password, optional'), { target: { value: 'example-password' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Invite' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(await screen.findByRole('alert')).toHaveTextContent('User created. They can sign in with their username and password.');
  });
});

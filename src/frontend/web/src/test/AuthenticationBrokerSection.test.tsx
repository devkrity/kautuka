import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, expect, it, vi } from 'vitest';
import { AuthenticationBrokerSection } from '@/features/settings/components/AuthenticationBrokerSection';

const api = vi.hoisted(() => ({ get: vi.fn(), put: vi.fn() }));
vi.mock('@/lib/apiClient', () => ({ apiClient: api }));

beforeEach(() => {
  api.get.mockReset(); api.put.mockReset();
  api.get.mockResolvedValue({ data: { active: { origin: 'https://auth.kautuka.io', googleSocialClientId: 'hosted-google', microsoftSocialClientId: 'hosted-ms' }, pending: null, switchPending: false } });
});

it('clears hosted social client IDs when an admin changes broker and shows restart guidance', async () => {
  api.put.mockImplementation(async (_path: string, value: unknown) => ({ data: { active: { origin: 'https://auth.kautuka.io', googleSocialClientId: 'hosted-google', microsoftSocialClientId: 'hosted-ms' }, pending: value, switchPending: true } }));
  render(<AuthenticationBrokerSection />);
  const origin = await screen.findByLabelText('Broker origin');
  fireEvent.change(origin, { target: { value: 'https://selfhost.example' } });
  expect(screen.getByLabelText('Google sign-in client ID')).toHaveValue('');
  expect(screen.getByLabelText('Microsoft sign-in client ID')).toHaveValue('');
  fireEvent.change(screen.getByLabelText('Google sign-in client ID'), { target: { value: 'custom-google' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save broker settings' }));
  await waitFor(() => expect(api.put).toHaveBeenCalledWith('/settings/system/authentication-broker', {
    origin: 'https://selfhost.example', googleSocialClientId: 'custom-google', microsoftSocialClientId: null,
  }));
  expect(await screen.findByRole('status')).toHaveTextContent('Restart Kautuka');
});

it('allows an uncertain save to be canceled', async () => {
  const active = { origin: 'https://auth.kautuka.io', googleSocialClientId: 'hosted-google', microsoftSocialClientId: 'hosted-ms' };
  api.get.mockResolvedValueOnce({ data: { active, pending: null, switchPending: false } })
    .mockResolvedValueOnce({ data: { active, pending: null, switchPending: true } });
  api.put.mockRejectedValueOnce(new Error('connection lost'))
    .mockResolvedValueOnce({ data: { active, pending: null, switchPending: false } });
  render(<AuthenticationBrokerSection />);
  fireEvent.change(await screen.findByLabelText('Broker origin'), { target: { value: 'https://selfhost.example' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save broker settings' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Cancel pending change' }));
  await waitFor(() => expect(api.put).toHaveBeenLastCalledWith('/settings/system/authentication-broker', active));
});

it('reports a saved change when the save response is lost after commit', async () => {
  const active = { origin: 'https://auth.kautuka.io', googleSocialClientId: 'hosted-google', microsoftSocialClientId: 'hosted-ms' };
  const pending = { origin: 'https://selfhost.example', googleSocialClientId: 'selfhost-google', microsoftSocialClientId: null };
  api.get.mockResolvedValueOnce({ data: { active, pending: null, switchPending: false } })
    .mockResolvedValueOnce({ data: { active, pending, switchPending: true } });
  api.put.mockRejectedValueOnce(new Error('response lost'));
  render(<AuthenticationBrokerSection />);
  fireEvent.change(await screen.findByLabelText('Broker origin'), { target: { value: pending.origin } });
  fireEvent.change(screen.getByLabelText('Google sign-in client ID'), { target: { value: 'selfhost-google' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save broker settings' }));
  expect(await screen.findByRole('status')).toHaveTextContent('Settings for https://selfhost.example are pending. Restart Kautuka');
  expect(screen.getByRole('status')).not.toHaveTextContent('Could not save');
  expect(screen.getByLabelText('Google sign-in client ID')).toHaveValue('selfhost-google');
  expect(screen.getByRole('button', { name: 'Cancel pending change' })).toBeEnabled();
});

it('requires a reload when both save and status requests fail', async () => {
  const active = { origin: 'https://auth.kautuka.io', googleSocialClientId: 'hosted', microsoftSocialClientId: null };
  const pending = { origin: 'https://selfhost.example', googleSocialClientId: null, microsoftSocialClientId: null };
  api.get.mockResolvedValueOnce({ data: { active, pending: null, switchPending: false } })
    .mockRejectedValueOnce(new Error('status unavailable'))
    .mockResolvedValueOnce({ data: { active, pending, switchPending: true } });
  api.put.mockRejectedValueOnce(new Error('response lost'));
  render(<AuthenticationBrokerSection />);
  fireEvent.change(await screen.findByLabelText('Broker origin'), { target: { value: pending.origin } });
  fireEvent.click(screen.getByRole('button', { name: 'Save broker settings' }));
  expect(await screen.findByRole('status')).toHaveTextContent('Save result is unknown');
  expect(screen.getByRole('button', { name: 'Save broker settings' })).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Reload settings' }));
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('https://selfhost.example are pending'));
  expect(screen.getByRole('button', { name: 'Cancel pending change' })).toBeEnabled();
});

it('treats a server error with an unavailable status read as an unknown outcome', async () => {
  api.get.mockResolvedValueOnce({ data: { active: { origin: 'https://auth.kautuka.io', googleSocialClientId: null, microsoftSocialClientId: null }, pending: null, switchPending: false } })
    .mockRejectedValueOnce(new Error('status unavailable'));
  api.put.mockRejectedValueOnce({ response: { status: 503 } });
  render(<AuthenticationBrokerSection />);
  fireEvent.change(await screen.findByLabelText('Broker origin'), { target: { value: 'https://selfhost.example' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save broker settings' }));
  expect(await screen.findByRole('status')).toHaveTextContent('Save result is unknown');
  expect(screen.getByRole('button', { name: 'Reload settings' })).toBeEnabled();
});

it('confirms cancellation after a lost response when the active settings are restored', async () => {
  const active = { origin: 'https://auth.kautuka.io', googleSocialClientId: 'hosted', microsoftSocialClientId: null };
  const pending = { origin: 'https://other.example', googleSocialClientId: null, microsoftSocialClientId: null };
  api.get.mockResolvedValueOnce({ data: { active, pending, switchPending: true } })
    .mockResolvedValueOnce({ data: { active, pending: null, switchPending: false } });
  api.put.mockRejectedValueOnce(new Error('response lost'));
  render(<AuthenticationBrokerSection />);
  fireEvent.click(await screen.findByRole('button', { name: 'Cancel pending change' }));
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Pending change canceled.'));
  expect(screen.queryByRole('button', { name: 'Cancel pending change' })).toBeNull();
});

it('clears pending broker IDs if its origin is edited again', async () => {
  api.get.mockResolvedValue({ data: { active: { origin: 'https://auth.kautuka.io', googleSocialClientId: 'hosted', microsoftSocialClientId: null },
    pending: { origin: 'https://broker-b.example', googleSocialClientId: 'broker-b', microsoftSocialClientId: null }, switchPending: true } });
  render(<AuthenticationBrokerSection />);
  expect(await screen.findByRole('status')).toHaveTextContent('Restart Kautuka');
  fireEvent.change(await screen.findByLabelText('Broker origin'), { target: { value: 'https://broker-c.example' } });
  expect(screen.getByLabelText('Google sign-in client ID')).toHaveValue('');
});

it('preserves the error if a different earlier change remains pending', async () => {
  const active = { origin: 'https://auth.kautuka.io', googleSocialClientId: 'hosted', microsoftSocialClientId: null };
  const pending = { origin: 'https://broker-b.example', googleSocialClientId: 'broker-b', microsoftSocialClientId: null };
  api.get.mockResolvedValue({ data: { active, pending, switchPending: true } });
  api.put.mockRejectedValue({ response: { data: { detail: 'Unable to save the new broker.' } } });
  render(<AuthenticationBrokerSection />);
  fireEvent.change(await screen.findByLabelText('Broker origin'), { target: { value: 'https://broker-c.example' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save broker settings' }));
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Unable to save the new broker. Settings for https://broker-b.example are pending.'));
});

it('shows why an existing mail connection blocks a broker change', async () => {
  api.put.mockRejectedValue({ response: { status: 409, data: { detail: 'Remove saved mail connections before changing the broker.' } } });
  render(<AuthenticationBrokerSection />);
  fireEvent.change(await screen.findByLabelText('Broker origin'), { target: { value: 'https://other.example' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save broker settings' }));
  expect(await screen.findByRole('status')).toHaveTextContent('Remove saved mail connections');
});

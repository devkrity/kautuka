import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, expect, it, vi } from 'vitest';
import { NetworkAccessLimitsSection } from '@/features/settings/components/NetworkAccessLimitsSection';

const api = vi.hoisted(() => ({ get: vi.fn(), put: vi.fn() }));
vi.mock('@/lib/apiClient', () => ({ apiClient: api }));

beforeEach(() => {
  api.get.mockReset(); api.put.mockReset();
  api.get.mockResolvedValue({ data: { global: 500, perUser: 64 } });
});

it('loads and saves the administrator configured limits', async () => {
  api.put.mockResolvedValue({ data: { global: 100, perUser: 20 } });
  render(<NetworkAccessLimitsSection />);
  fireEvent.change(await screen.findByLabelText('Total pending requests'), { target: { value: '100' } });
  fireEvent.change(screen.getByLabelText('Pending requests per user'), { target: { value: '20' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save network access settings' }));
  await waitFor(() => expect(api.put).toHaveBeenCalledWith('/settings/system/network-access', { global: 100, perUser: 20 }));
  expect(await screen.findByRole('status')).toHaveTextContent('saved');
});

it('rejects a per-user limit above the total', async () => {
  render(<NetworkAccessLimitsSection />);
  fireEvent.change(await screen.findByLabelText('Total pending requests'), { target: { value: '10' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save network access settings' }));
  expect(screen.getByRole('status')).toHaveTextContent('cannot exceed');
  expect(api.put).not.toHaveBeenCalled();
});

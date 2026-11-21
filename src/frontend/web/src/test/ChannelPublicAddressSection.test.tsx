import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ChannelPublicAddressSection } from '@/features/settings/components/ChannelPublicAddressSection';

const api = vi.hoisted(() => ({ get: vi.fn(), put: vi.fn() }));
vi.mock('@/lib/apiClient', () => ({ apiClient: api }));

describe('channel link address', () => {
  it('saves the address configured by the administrator', async () => {
    api.get.mockResolvedValue({ data: { address: null } });
    api.put.mockResolvedValue({ data: { address: 'https://kautuka.example' } });
    render(<ChannelPublicAddressSection />);
    fireEvent.change(screen.getByLabelText('Kautuka address for channel links'), { target: { value: 'https://kautuka.example' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save address' }));
    await waitFor(() => expect(api.put).toHaveBeenCalledWith('/settings/system/channels/address', { address: 'https://kautuka.example' }));
    expect(await screen.findByText('Kautuka address saved.')).toBeTruthy();
  });
});

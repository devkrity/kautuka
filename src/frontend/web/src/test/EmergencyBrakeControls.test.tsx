import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import { EmergencyBrakeControls } from '@/features/network-access/components/EmergencyBrakeControls';

it('reports an unconfirmed pause rather than silently closing the dialog', async () => {
  const update = vi.fn().mockRejectedValue(new Error('connection lost'));
  render(<EmergencyBrakeControls currentUser={{ id: 'alice', role: 'user' }} onUpdateUserBrake={update} />);
  fireEvent.click(screen.getByRole('button', { name: 'Pause all network access for my agents' }));
  fireEvent.click(screen.getByRole('button', { name: 'Pause network' }));
  expect(await screen.findByRole('alert')).toHaveTextContent('could not be confirmed');
  expect(screen.getByRole('dialog')).toBeInTheDocument();
  await waitFor(() => expect(screen.getByRole('button', { name: 'Pause network' })).toBeEnabled());
});

it('asks before resuming network access for everyone', async () => {
  const update = vi.fn().mockResolvedValue(undefined);
  render(<EmergencyBrakeControls currentUser={{ id: 'admin', role: 'admin' }}
    brake={{ userPaused: false, systemPaused: true, userLockedByAdmin: false }}
    onUpdateUserBrake={vi.fn()} onUpdateSystemBrake={update} />);
  fireEvent.click(screen.getByRole('button', { name: 'Resume network access for everyone' }));
  expect(update).not.toHaveBeenCalled();
  expect(screen.getByRole('dialog')).toHaveTextContent('everyone');
  fireEvent.click(screen.getByRole('button', { name: 'Confirm resume' }));
  await waitFor(() => expect(update).toHaveBeenCalledWith(false));
});

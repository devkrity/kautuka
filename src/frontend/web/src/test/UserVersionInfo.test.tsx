import { UserVersionInfo } from '@/features/auth';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

describe('UserVersionInfo', () => {
  it('shows compact update status and exposes actions when expanded', () => {
    const apply = vi.fn();
    const rollback = vi.fn();

    render(
      <UserVersionInfo
        currentVersion="1.0.0"
        updateTitle="Update ready: 1.1.0"
        updateDescription="A newer release is available."
        updateTone="available"
        availableVersion="1.1.0"
        releaseChannel="stable"
        canApplyUpdate
        onApplyUpdate={apply}
        canRollbackUpdate
        onRollbackUpdate={rollback}
      />
    );

    fireEvent.click(screen.getByText('Update ready: 1.1.0'));

    expect(screen.getByText('Current: 1.0.0')).toBeInTheDocument();
    expect(screen.getByText('Available: 1.1.0')).toBeInTheDocument();
    expect(screen.getByText('Track: stable')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    fireEvent.click(screen.getByRole('button', { name: 'Roll back' }));

    expect(apply).toHaveBeenCalledTimes(1);
    expect(rollback).toHaveBeenCalledTimes(1);
  });
});

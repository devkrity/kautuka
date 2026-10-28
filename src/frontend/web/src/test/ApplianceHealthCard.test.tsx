import { render, screen } from '@testing-library/react';
import { ApplianceHealthCard } from '@/features/dashboard';

describe('ApplianceHealthCard', () => {
  it('keeps a full release identifier inside the responsive card', () => {
    const installedVersion =
      '1.0.0+0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef';

    render(
      <ApplianceHealthCard
        health={{
          installedVersion,
          lastUpdateAppliedAtUtc: null,
          sidecarsStagedAtUtc: null,
          sidecarCount: 0,
        }}
      />
    );

    const version = screen.getByText(installedVersion);
    expect(version).toHaveClass('break-all', 'leading-snug');
    expect(version).toHaveAttribute('title', installedVersion);
    expect(version.closest('.card-interactive')).toHaveClass('min-w-0', 'overflow-hidden');
  });
});

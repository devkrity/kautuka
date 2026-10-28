import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { SuccessMilestone } from '@/features/onboarding/components/SuccessMilestone';

describe('SuccessMilestone', () => {
  it('repeats the runtime boundary sentence on the success step', () => {
    render(
      <SuccessMilestone
        telegramConfigured={false}
        onContinue={vi.fn()}
        onReviewAbilities={vi.fn()}
        onOpenSettings={vi.fn()}
      />
    );

    expect(screen.getByText('Your helper is ready')).toBeInTheDocument();
    expect(
      screen.getByText(
        'Your AI model can run locally or in the cloud, but Kautuka runs here and performs tool work here.'
      )
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Start chatting' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Review abilities' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Open settings' })).toBeInTheDocument();
  });

  it('shows the Telegram test hint when Telegram was configured in onboarding', () => {
    render(
      <SuccessMilestone
        telegramConfigured
        onContinue={vi.fn()}
        onReviewAbilities={vi.fn()}
        onOpenSettings={vi.fn()}
      />
    );

    expect(screen.getByText('Telegram is connected')).toBeInTheDocument();
    expect(
      screen.getByText(
        'Later, if you want to verify it, go to Settings → Users → Telegram Settings and use Send Telegram Test.'
      )
    ).toBeInTheDocument();
  });
});

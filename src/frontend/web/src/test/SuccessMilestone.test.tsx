import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { SuccessMilestone } from '@/features/onboarding/components/SuccessMilestone';

describe('SuccessMilestone', () => {
  it('offers the first chat before optional setup', () => {
    const startChat = vi.fn();
    render(
      <SuccessMilestone
        telegramConfigured={false}
        onContinue={startChat}
        onReviewAbilities={vi.fn()}
        onOpenSettings={vi.fn()}
      />
    );

    expect(screen.getByText('Your helper is ready')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Chat with my helper' }));
    expect(startChat).toHaveBeenCalledOnce();
    expect(screen.getByRole('button', { name: 'Explore optional setup' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Open settings' })).toBeInTheDocument();
  });

  it('disables the actions and shows progress while creating the chat', () => {
    render(
      <SuccessMilestone startingChat onContinue={vi.fn()} onReviewAbilities={vi.fn()} onOpenSettings={vi.fn()} />
    );
    expect(screen.getByRole('button', { name: 'Starting chat…' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Explore optional setup' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Open settings' })).toBeDisabled();
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

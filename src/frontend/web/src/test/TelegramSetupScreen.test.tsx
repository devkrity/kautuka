import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { TelegramSetupScreen } from '@/features/onboarding/components/TelegramSetupScreen';

describe('TelegramSetupScreen', () => {
  it('explains optional Telegram setup and where to configure it later', () => {
    render(
      <TelegramSetupScreen
        agentName="Desk Assistant"
        telegramAccessCode=""
        telegramChatId=""
        canSave={false}
        saving={false}
        onAccessCodeChange={vi.fn()}
        onChatIdChange={vi.fn()}
        onBack={vi.fn()}
        onSkip={vi.fn()}
        onSave={vi.fn()}
      />
    );

    expect(screen.getByText('Connect Telegram')).toBeInTheDocument();
    expect(
      screen.getByText(
        'While your machine and Kautuka are running, you can message the bot from Telegram when you are away from your desk.'
      )
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        'When this agent needs approval later, Telegram can be used as one of the places where you review and respond to those requests.'
      )
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        'You can skip this now and set it up later in Settings under Users, inside your Telegram Settings section.'
      )
    ).toBeInTheDocument();
    expect(screen.getByText(/In Telegram, open/i)).toBeInTheDocument();
    expect(screen.getByText(/third-party ID bot or website/)).toBeInTheDocument();
    expect(screen.getByLabelText(/Telegram private chat ID/i)).toBeInTheDocument();
    expect(
      screen.getByText(
        'Kautuka will connect one private Telegram chat to Desk Assistant. Use the keyboard in that chat to choose an agent, switch sessions, or start a new session. Topics and groups are not needed. You can change the bot and approval settings later in Settings → Users → Telegram Settings.'
      )
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Skip for now' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Connect bot' })).toBeInTheDocument();
  });

  it('accepts a bot token and explicit Telegram chat device ID without a pairing command', () => {
    render(
      <TelegramSetupScreen
        agentName="Desk Assistant"
        telegramAccessCode="secret"
        telegramChatId="123456789"
        canSave={true}
        saving={false}
        onAccessCodeChange={vi.fn()}
        onChatIdChange={vi.fn()}
        onBack={vi.fn()}
        onSkip={vi.fn()}
        onSave={vi.fn()}
      />
    );

    expect(screen.getByLabelText(/Telegram private chat ID/i)).toHaveValue('123456789');
    expect(screen.queryByText(/\/connect/i)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Connect bot' })).toBeEnabled();
  });
});

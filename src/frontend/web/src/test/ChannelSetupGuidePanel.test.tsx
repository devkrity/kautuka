import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ChannelSetupGuidePanel } from '@/features/auth/components/ChannelSetupGuidePanel';
import { getChannelSetupGuide } from '@/features/auth/components/channelSetupGuides';

describe('ChannelSetupGuidePanel', () => {
  it('renders numbered accessible guidance and a safe provider link', () => {
    render(
      <ChannelSetupGuidePanel
        guide={getChannelSetupGuide('telegram')}
        expanded
        onCopyArtifact={vi.fn().mockResolvedValue(true)}
      />
    );

    expect(screen.getByText('Guided setup (3 steps)').closest('details')).toHaveAttribute('open');
    expect(screen.getByText(/Step 1:/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Open BotFather/ })).toHaveAttribute(
      'href',
      'https://t.me/BotFather'
    );
    expect(screen.getByRole('link', { name: /Open BotFather/ })).toHaveAttribute(
      'rel',
      'noreferrer'
    );
    expect(screen.getByText(/Find the Telegram private chat ID/)).toBeInTheDocument();
    expect(screen.getByText(/official Bot API getUpdates/i)).toBeInTheDocument();
    expect(screen.queryByText(/one-time \/connect/i)).not.toBeInTheDocument();
  });

  it('fetches the authenticated Slack app link before opening the setup portal', async () => {
    const appUrl = 'https://api.slack.com/apps?new_app=1&manifest_json=%7B%7D';
    const onOpenPortal = vi.fn().mockResolvedValue(appUrl);
    const popup = {
      location: { href: 'about:blank' },
      opener: window,
      close: vi.fn(),
    } as unknown as Window;
    const openSpy = vi.spyOn(window, 'open').mockReturnValue(popup);

    render(
      <ChannelSetupGuidePanel
        guide={getChannelSetupGuide('slack')}
        expanded
        onOpenPortal={onOpenPortal}
        onCopyArtifact={vi.fn().mockResolvedValue(true)}
      />
    );

    const button = screen.getByRole('button', { name: /Create Slack app/ });
    expect(button).not.toHaveAttribute('href');
    fireEvent.click(button);

    expect(openSpy).toHaveBeenCalledWith('about:blank', '_blank');
    await waitFor(() => expect(onOpenPortal).toHaveBeenCalledOnce());
    await waitFor(() => expect(popup.location.href).toBe(appUrl));
    expect(screen.queryByRole('button', { name: /manifest/i })).not.toBeInTheDocument();
  });
});

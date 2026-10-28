import { describe, expect, it } from 'vitest';
import {
  getChannelSetupGuide,
  getChannelSetupOutcome,
} from '@/features/auth/components/channelSetupGuides';

describe('channel setup guides', () => {
  it.each([
    ['telegram', 'https://t.me/BotFather', 'Bot-token validation'],
    ['slack', '/api/settings/channels/slack/create-app', 'Checks Slack can reach your bot and start a live connection'],
  ])('provides safe guided setup for %s', (providerKey, portalUrl, automatedCapability) => {
    const guide = getChannelSetupGuide(providerKey);

    expect(guide.portalUrl).toBe(portalUrl);
    expect(guide.steps.length).toBeGreaterThanOrEqual(3);
    expect(guide.automated).toContain(automatedCapability);
    expect(guide.steps.flatMap((step) => [step.title, step.detail]).join(' ')).not.toContain(
      'http://'
    );
  });

  it('uses a non-secret-bearing fallback for future providers', () => {
    const guide = getChannelSetupGuide('future-provider');

    expect(guide.steps).toHaveLength(3);
    expect(guide.automated).toContain('Credential isolation');
    expect(guide.intro.toLowerCase()).not.toContain('token value');
  });

  it('resolves provider keys case-insensitively', () => {
    expect(getChannelSetupGuide('Slack').portalLabel).toBe('Create Slack app');
  });

  it('makes Telegram chat routing explicit without recommending a third-party ID bot', () => {
    const guide = getChannelSetupGuide('telegram');
    const guidance = guide.steps.flatMap((step) => [step.title, step.detail]).join(' ');

    expect(guide.destinationLabel).toBe('Telegram private chat ID');
    expect(guide.destinationHelp).toContain('Required');
    expect(guidance).toContain('official Bot API getUpdates');
    expect(guidance).not.toContain('one-time /connect');
    expect(guidance).toContain('third-party ID bot or website');
  });

  it('explains exactly where to generate both Slack tokens', () => {
    const guide = getChannelSetupGuide('slack');
    const guidance = guide.steps.flatMap((step) => [step.title, step.detail]).join(' ');

    expect(guide.steps).toHaveLength(5);
    expect(guide.portalUrl).toContain('/api/settings/channels/slack/create-app');
    expect(guidance).toContain('Choose your workspace, then click Create');
    expect(guidance).toContain('Basic Information');
    expect(guidance).toContain('App-Level Tokens');
    expect(guidance).toContain('Generate Token');
    expect(guidance).toContain('connections:write');
    expect(guidance).toContain('OAuth & Permissions');
    expect(guidance).toContain('Bot User OAuth Token');
    expect(guidance).toContain('private DM-only app settings');
  });

  it('turns provider errors into actionable recovery without exposing raw details', () => {
    const outcome = getChannelSetupOutcome('Slack', {
      success: false,
      status: 'failed',
      detail: 'invalid_auth: xoxb-sensitive-token',
    });

    expect(outcome.success).toBe(false);
    expect(outcome.message).toContain('fresh token');
    expect(outcome.message).not.toContain('xoxb-sensitive-token');
  });

  it('shows the actionable private-setup error for mismatched Slack scopes', () => {
    const outcome = getChannelSetupOutcome('Slack', {
      success: false,
      status: 'failed',
      detail: "This Slack app doesn't match Kautuka's private setup. Recreate it from the manifest link.",
    });

    expect(outcome.success).toBe(false);
    expect(outcome.message).toBe(
      "This Slack app doesn't match Kautuka's private setup. Recreate it from the manifest link."
    );
  });

  it('shows the single-workspace Slack connection conflict', () => {
    const outcome = getChannelSetupOutcome('Slack', {
      success: false,
      status: 'slack_workspace_in_use',
      detail: 'slack_workspace_in_use',
    });
    expect(outcome.success).toBe(false);
    expect(outcome.message).toBe(
      'This Slack workspace is already connected to another Kautuka member. For now each workspace can be connected by one member.'
    );
  });

  it('explains asynchronous connection progress', () => {
    expect(
      getChannelSetupOutcome('Telegram', {
        success: true,
        status: 'accepted',
      }).message
    ).toContain('still starting');
  });

});

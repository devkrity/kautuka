import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ChannelSettingsSection } from '@/features/settings/components/ChannelSettingsSection';

const api = vi.hoisted(() => ({ get: vi.fn(), put: vi.fn() }));
vi.mock('@/lib/apiClient', () => ({ apiClient: api }));

describe('platform channel settings', () => {
  beforeEach(() => { api.get.mockReset(); api.put.mockReset(); });
  it('enables Telegram only for the channel service', async () => {
    const rules: Record<string, unknown>[] = [];
    let revision = 7;
    api.get.mockImplementation(async () => ({ data: { revision, rules: [...rules] } }));
    api.put.mockImplementation(async (_url: string, body: Record<string, unknown>) => {
      const index = rules.findIndex(rule => rule.destination === body.destination);
      if (index < 0) rules.push(body); else rules[index] = body;
      revision += 1;
      return { data: body };
    });
    render(<ChannelSettingsSection />);
    fireEvent.click(await screen.findByRole('button', { name: 'Enable Telegram' }));
    await waitFor(() => expect(api.put).toHaveBeenCalledWith('/network-access/rules', expect.objectContaining({
      scopeKind: 'agent', userId: 'system', agentId: 'channelhost', destination: 'api.telegram.org',
      allowed: true, expectedRevision: 7,
    })));
    fireEvent.click(await screen.findByRole('button', { name: 'Pause Telegram' }));
    await waitFor(() => expect(api.put).toHaveBeenCalledWith('/network-access/rules', expect.objectContaining({
      destination: 'api.telegram.org', allowed: false, expectedRevision: 8,
    })));
    expect(await screen.findByText('Channel paused. Saved account settings are kept.')).toBeTruthy();
  });
  it('enables Slack with one channel-service rule for its host and subdomains', async () => {
    const rules: Record<string, unknown>[] = [];
    api.get.mockImplementation(async () => ({ data: { revision: 1, rules: [...rules] } }));
    api.put.mockImplementation(async (_url: string, body: Record<string, unknown>) => {
      rules.push(body);
      return { data: body };
    });
    render(<ChannelSettingsSection />);
    fireEvent.click(await screen.findByRole('button', { name: 'Enable Slack' }));
    await waitFor(() => expect(api.put).toHaveBeenCalledTimes(1));
    expect(api.put).toHaveBeenCalledWith('/network-access/rules', expect.objectContaining({
      userId: 'system', agentId: 'channelhost', destination: 'slack.com', allowed: true,
    }));
  });

  it('shows the paused release hosts and writes only scoped permissions after approval', async () => {
    const hosts = ['api.github.com', 'github.com', 'release-assets.githubusercontent.com'];
    const rules = hosts.map(destination => ({
      scopeKind: 'agent', userId: 'system', agentId: 'updateservice', destination, port: 443, allowed: false,
    }));
    let revision = 4;
    api.get.mockImplementation(async () => ({ data: { revision, rules: [...rules] } }));
    api.put.mockImplementation(async (_url: string, body: (typeof rules)[number]) => {
      const index = rules.findIndex(rule => rule.destination === body.destination);
      rules[index] = body;
      revision += 1;
      return { data: body };
    });
    render(<ChannelSettingsSection />);
    expect(await screen.findByText('In-app updates: Paused')).toBeVisible();
    expect(screen.getByText(hosts.join(', '))).toBeVisible();
    expect(api.put).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Enable in-app updates' }));
    expect(await screen.findByText('In-app updates enabled.')).toBeVisible();
    expect(api.put).toHaveBeenCalledTimes(3);
    hosts.forEach((destination, index) => {
      expect(api.put).toHaveBeenNthCalledWith(index + 1, '/network-access/rules', expect.objectContaining({
        scopeKind: 'agent', userId: 'system', agentId: 'updateservice', destination,
        port: 443, allowed: true, allowLan: false, expectedRevision: 4 + index,
      }));
    });
  });

  it('can pause a saved service permission while a broader rule blocks it', async () => {
    const grant = { scopeKind: 'agent', userId: 'system', agentId: 'channelhost', destination: 'slack.com', port: 443, allowed: true };
    const block = { scopeKind: 'system', destination: 'slack.com', port: 443, allowed: false };
    const rules = [grant, block];
    api.get.mockImplementation(async () => ({ data: { revision: 6, rules: [...rules] } }));
    api.put.mockImplementation(async (_url: string, body: typeof grant) => {
      rules[0] = body;
      return { data: body };
    });
    render(<ChannelSettingsSection />);
    expect(await screen.findByText('Slack: Blocked by another network rule')).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Pause Slack' }));
    expect(await screen.findByText('Slack: Paused')).toBeVisible();
    expect(api.put).toHaveBeenCalledWith('/network-access/rules', expect.objectContaining({
      userId: 'system', agentId: 'channelhost', destination: 'slack.com', allowed: false,
    }));
  });
});

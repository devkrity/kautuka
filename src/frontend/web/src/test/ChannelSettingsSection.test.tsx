import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ChannelSettingsSection } from '@/features/settings/components/ChannelSettingsSection';

const api = vi.hoisted(() => ({ get: vi.fn(), put: vi.fn() }));
vi.mock('@/lib/apiClient', () => ({ apiClient: api }));

function views(rules: Array<Record<string, unknown>>) {
  const ids = [...new Set(rules.map(rule => rule.setupService).filter(Boolean))];
  return ids.map(serviceId => {
    const targets = rules.filter(rule => rule.setupService === serviceId);
    const granted = targets.filter(rule => rule.allowed).length;
    const effective = targets.filter(target => target.allowed && !rules.some(rule => rule.scopeKind === 'system'
      && rule.destination === target.destination && rule.port === target.port && !rule.allowed)).length;
    return { serviceId, total: targets.length, granted, effective };
  });
}

describe('platform channel settings', () => {
  beforeEach(() => { api.get.mockReset(); api.put.mockReset(); });
  it('refreshes the current state when the service revision is stale', async () => {
    const rule = { setupService: 'updates', scopeKind: 'agent', userId: 'system', agentId: 'updateservice',
      destination: 'api.github.com', destKind: 'host', port: 443, allowed: false };
    api.get.mockResolvedValueOnce({ data: { revision: 2, rules: [rule], services: views([rule]) } })
      .mockResolvedValueOnce({ data: { revision: 3, rules: [{ ...rule, allowed: true }], services: views([{ ...rule, allowed: true }]) } });
    api.put.mockRejectedValue(new Error('revision conflict'));
    render(<ChannelSettingsSection />);
    fireEvent.click(await screen.findByRole('button', { name: 'Enable in-app updates' }));
    expect(await screen.findByText('In-app updates: Allowed by this setting')).toBeVisible();
    expect(api.put).toHaveBeenCalledTimes(1);
    expect(api.put).toHaveBeenCalledWith('/network-access/services', { serviceId: 'updates', allowed: true, expectedRevision: 2 });
  });
  it('enables Telegram only for the channel service', async () => {
    const rules: Record<string, unknown>[] = [{
      setupService: 'telegram', scopeKind: 'agent', userId: 'system', agentId: 'channelhost',
      destination: 'api.telegram.org', destKind: 'host', port: 443, allowed: false,
    }];
    let revision = 7;
    api.get.mockImplementation(async () => ({ data: { revision, rules: [...rules], services: views(rules) } }));
    api.put.mockImplementation(async (_url: string, body: Record<string, unknown>) => {
      rules[0] = { ...rules[0], allowed: body.allowed };
      revision += 1;
      return { data: { status: body.allowed ? 'allowed' : 'paused', revision } };
    });
    render(<ChannelSettingsSection />);
    fireEvent.click(await screen.findByRole('button', { name: 'Enable Telegram' }));
    await waitFor(() => expect(api.put).toHaveBeenCalledWith('/network-access/services', expect.objectContaining({
      serviceId: 'telegram', allowed: true, expectedRevision: 7,
    })));
    fireEvent.click(await screen.findByRole('button', { name: 'Pause Telegram' }));
    await waitFor(() => expect(api.put).toHaveBeenCalledWith('/network-access/services', expect.objectContaining({
      serviceId: 'telegram', allowed: false, expectedRevision: 8,
    })));
    expect(await screen.findByText('Channel paused. Saved account settings are kept.')).toBeTruthy();
  });
  it('enables Slack with one channel-service rule for its host and subdomains', async () => {
    const rules: Record<string, unknown>[] = [{
      setupService: 'slack', scopeKind: 'agent', userId: 'system', agentId: 'channelhost',
      destination: 'slack.com', destKind: 'host', port: 443, allowed: false,
    }];
    api.get.mockImplementation(async () => ({ data: { revision: 1, rules: [...rules], services: views(rules) } }));
    api.put.mockImplementation(async (_url: string, body: Record<string, unknown>) => {
      rules[0] = { ...rules[0], allowed: body.allowed };
      return { data: { status: body.allowed ? 'allowed' : 'paused' } };
    });
    render(<ChannelSettingsSection />);
    fireEvent.click(await screen.findByRole('button', { name: 'Enable Slack' }));
    await waitFor(() => expect(api.put).toHaveBeenCalledTimes(1));
    expect(api.put).toHaveBeenCalledWith('/network-access/services', expect.objectContaining({
      serviceId: 'slack', allowed: true,
    }));
  });

  it('shows the paused release hosts and writes only scoped permissions after approval', async () => {
    const hosts = ['api.github.com', 'github.com', 'release-assets.githubusercontent.com'];
    const rules = hosts.map(destination => ({
      setupService: 'updates', scopeKind: 'agent', userId: 'system', agentId: 'updateservice',
      destination, destKind: 'host', port: 443, allowed: false,
    }));
    let revision = 4;
    api.get.mockImplementation(async () => ({ data: { revision, rules: [...rules], services: views(rules) } }));
    api.put.mockImplementation(async (_url: string, body: { allowed: boolean }) => {
      rules.forEach(rule => { rule.allowed = body.allowed; });
      revision += 1;
      return { data: { status: body.allowed ? 'allowed' : 'paused', revision } };
    });
    render(<ChannelSettingsSection />);
    expect(await screen.findByText('In-app updates: Paused')).toBeVisible();
    expect(screen.getByText(hosts.join(', '))).toBeVisible();
    expect(api.put).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Enable in-app updates' }));
    expect(await screen.findByText('In-app updates enabled.')).toBeVisible();
    expect(api.put).toHaveBeenCalledTimes(1);
    expect(api.put).toHaveBeenCalledWith('/network-access/services', {
      serviceId: 'updates', allowed: true, expectedRevision: 4,
    });
  });

  it('shows suffix list maintenance allowed by default and pauses it separately from updates', async () => {
    const suffix = { setupService: 'suffix-list', scopeKind: 'agent', userId: 'system',
      agentId: 'updateservice', destination: 'publicsuffix.org', destKind: 'host', port: 443, allowed: true };
    const release = { ...suffix, setupService: 'updates', destination: 'api.github.com', allowed: false };
    const rules = [suffix, release];
    api.get.mockImplementation(async () => ({ data: { revision: 1, rules: [...rules], services: views(rules) } }));
    api.put.mockImplementation(async (_url: string, body: { allowed: boolean }) => {
      rules[0] = { ...rules[0], allowed: body.allowed };
      return { data: { status: body.allowed ? 'allowed' : 'paused' } };
    });
    render(<ChannelSettingsSection />);
    expect(await screen.findByText('Public Suffix List refresh: Allowed by this setting')).toBeVisible();
    expect(screen.getByText('publicsuffix.org')).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Pause suffix list refresh' }));
    expect(await screen.findByText('Public Suffix List refresh: Paused')).toBeVisible();
    expect(screen.getByText('In-app updates: Paused')).toBeVisible();
    expect(api.put).toHaveBeenCalledTimes(1);
    expect(api.put).toHaveBeenCalledWith('/network-access/services', expect.objectContaining({
      serviceId: 'suffix-list', allowed: false,
    }));
  });

  it('can pause a saved service permission while a broader rule blocks it', async () => {
    const grant = { setupService: 'slack', scopeKind: 'agent', userId: 'system', agentId: 'channelhost',
      destination: 'slack.com', destKind: 'host', port: 443, allowed: true };
    const block = { scopeKind: 'system', destination: 'slack.com', port: 443, allowed: false };
    const rules = [grant, block];
    api.get.mockImplementation(async () => ({ data: { revision: 6, rules: [...rules], services: views(rules) } }));
    api.put.mockImplementation(async (_url: string, body: { allowed: boolean }) => {
      rules[0] = { ...rules[0], allowed: body.allowed };
      return { data: { status: body.allowed ? 'allowed' : 'paused' } };
    });
    render(<ChannelSettingsSection />);
    expect(await screen.findByText('Slack: Blocked by another network rule')).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Pause Slack' }));
    expect(await screen.findByText('Slack: Paused')).toBeVisible();
    expect(api.put).toHaveBeenCalledWith('/network-access/services', expect.objectContaining({
      serviceId: 'slack', allowed: false,
    }));
  });

  it('shows built-in model permissions separately from a helper’s website access', async () => {
    const rule = { setupService: 'model-providers', scopeKind: 'agent', userId: 'system',
      agentId: 'gateway', destination: 'api.openai.com', destKind: 'host', port: 443, allowed: true };
    const rules = [rule, { ...rule, destination: 'api.anthropic.com', allowed: false },
      { ...rule, destination: 'api.groq.com' },
      { scopeKind: 'system', destination: 'api.groq.com', port: 443, allowed: false },
      { ...rule, setupService: undefined, userId: 'person', agentId: 'helper', destination: 'example.com' }];
    api.get.mockResolvedValue({ data: { revision: 1, rules, services: views(rules) } });

    render(<ChannelSettingsSection />);

    expect(await screen.findByText('Built-in model providers')).toBeVisible();
    expect(screen.getByText(/helpers have separate permissions for websites and connectors/)).toBeVisible();
    expect(screen.getByText('api.openai.com · Allowed by its rule')).toBeVisible();
    expect(screen.getByText('api.anthropic.com · Blocked by its rule')).toBeVisible();
    expect(screen.getByText('api.groq.com · Blocked by another network rule')).toBeVisible();
    expect(screen.queryByText(/example\.com/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /model providers/i })).not.toBeInTheDocument();
  });
});

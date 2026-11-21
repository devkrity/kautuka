import { beforeEach, describe, expect, it, vi } from 'vitest';
import { refreshBuiltInTools, useBuiltInToolRefresh } from '@/features/connectors/api/refreshBuiltInTools';
import { renderHook, waitFor } from '@testing-library/react';

const mocks = vi.hoisted(() => ({ fetch: vi.fn(), test: vi.fn(), warning: vi.fn() }));
vi.mock('@/features/connectors/api/connectors', () => ({ fetchConnectors: mocks.fetch, testConnector: mocks.test }));
vi.mock('sonner', () => ({ toast: { warning: mocks.warning } }));

describe('bundled connector refresh', () => {
  beforeEach(() => { mocks.fetch.mockReset(); mocks.test.mockReset(); mocks.warning.mockReset(); });
  it('does no background work until enabled for an administrator', async () => {
    mocks.fetch.mockResolvedValue([]);
    const { rerender } = renderHook(({ enabled }) => useBuiltInToolRefresh(enabled), { initialProps: { enabled: false } });
    expect(mocks.fetch).not.toHaveBeenCalled();
    rerender({ enabled: true });
    await waitFor(() => expect(mocks.fetch).toHaveBeenCalledTimes(1));
  });
  it('refreshes stale enabled built-ins once using the saved configuration', async () => {
    const connector = { id: 'kautuka-google-mail', name: 'Google Mail', scope: 'system', enabled: true,
      toolCount: null, updatedAt: 'v2', transport: 'container', containerArgs: [], envKeys: [] };
    mocks.fetch.mockResolvedValue([connector, { ...connector, id: 'kautuka-m365-mail', enabled: false },
      { ...connector, id: 'other-connector' }, { ...connector, id: 'kautuka-web-tools', toolCount: 3 }]);
    mocks.test.mockResolvedValue({ success: true });
    const attempted = new Map<string, number>();
    expect(await refreshBuiltInTools(attempted, () => true)).toBe(true);
    expect(mocks.test).toHaveBeenCalledTimes(1);
    expect(mocks.test).toHaveBeenCalledWith(expect.objectContaining({ id: connector.id, testEnv: {} }));
    expect(await refreshBuiltInTools(attempted, () => true)).toBe(false);
    expect(mocks.test).toHaveBeenCalledTimes(1);
  });
  it('retries a failed refresh once and tells the admin if it still fails', async () => {
    mocks.fetch.mockResolvedValue([{ id: 'kautuka-web-tools', name: 'Web Tools', scope: 'system', enabled: true,
      toolCount: null, updatedAt: 'v2', transport: 'container', containerArgs: [], envKeys: [] }]);
    mocks.test.mockResolvedValue({ success: false });
    const attempted = new Map<string, number>();
    await refreshBuiltInTools(attempted, () => true);
    expect(mocks.warning).not.toHaveBeenCalled();
    await refreshBuiltInTools(attempted, () => true);
    await refreshBuiltInTools(attempted, () => true);
    expect(mocks.test).toHaveBeenCalledTimes(2);
    expect(mocks.warning).toHaveBeenCalledTimes(1);
  });
});

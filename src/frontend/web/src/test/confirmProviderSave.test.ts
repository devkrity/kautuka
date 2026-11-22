import { afterEach, describe, expect, it, vi } from 'vitest';
import { confirmProviderSave } from '@/features/settings/utils/confirmProviderSave';

describe('provider network permission confirmation', () => {
  const permission = { destination: '192.168.1.12', port: 11434, allowLan: true, revision: '10' };
  const challenge = { response: { status: 409, data: permission } };
  const body = { id: 'local-ollama', settings: { base_url: 'http://192.168.1.12:11434' } };

  afterEach(() => vi.restoreAllMocks());

  it('shows the systemwide destination and retries with the same permission', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true);
    const save = vi.fn().mockRejectedValueOnce(challenge).mockResolvedValueOnce({});
    await expect(confirmProviderSave(save, body)).resolves.toBe(true);
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('everyone on this appliance to reach 192.168.1.12:11434'));
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('includes access to the local network'));
    expect(save).toHaveBeenNthCalledWith(2, { ...body, networkPermission: permission });
  });

  it('stops after the first request if the owner declines', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(false);
    const save = vi.fn().mockRejectedValueOnce(challenge);
    await expect(confirmProviderSave(save, body)).resolves.toBe(false);
    expect(save).toHaveBeenCalledTimes(1);
  });

  it('leaves existing restrictions in the normal error path', async () => {
    const confirm = vi.spyOn(window, 'confirm');
    const restricted = { response: { status: 409, data: { detail: 'Access restricted.' } } };
    await expect(confirmProviderSave(vi.fn().mockRejectedValue(restricted), body)).rejects.toBe(restricted);
    expect(confirm).not.toHaveBeenCalled();
  });
});

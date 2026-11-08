import { apiClient } from '@/lib/apiClient';
import { networkAccessSchema } from '@/features/network-access/networkAccess';

export type ProviderNetworkBlock = { destination: string; port: number };

// Use the existing revision-checked policy API. Never retry provider work until
// Manager confirms that the new policy is active.
export async function allowProviderNetworkAccess(block: ProviderNetworkBlock): Promise<void> {
  const current = networkAccessSchema.parse((await apiClient.get('/network-access')).data);
  if (!current.enabled)
    throw new Error('Network control is disabled. Retry the provider connection.');
  const changed = networkAccessSchema.parse(
    (
      await apiClient.put('/network-access/rules', {
        identity: {
          scopeType: 'core',
          userId: 'system',
          agentId: 'gateway',
          role: 'core',
          componentId: 'gateway',
        },
        destination: block.destination,
        port: block.port,
        allowed: true,
        expectedRevision: current.revision,
      })
    ).data
  );
  let active = changed;
  for (let attempt = 0; attempt < 20; attempt++) {
    if (active.revision !== changed.revision)
      throw new Error(
        'Network policy changed while permission was activating. Retry setup to check the current policy.'
      );
    if (active.activeRevision === changed.revision && active.activationState === 'active') return;
    if (active.lastError || active.activationState === 'failed')
      throw new Error(
        `Network permission could not be activated: ${active.lastError || 'activation failed'}`
      );
    await new Promise((resolve) => setTimeout(resolve, 500));
    active = networkAccessSchema.parse(
      (await apiClient.get('/network-access', { params: { summary: true } })).data
    );
  }
  throw new Error(
    'Network permission was saved but is not active yet. Wait a moment, then retry setup.'
  );
}

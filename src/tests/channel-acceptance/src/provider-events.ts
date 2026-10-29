import type { Provider, ProviderSimulatorControl } from './model.js';

export async function providerConnectionCount(simulator: ProviderSimulatorControl, provider: Provider): Promise<number> {
  const transcript = (await simulator.exportEvidence()).transcript;
  const operation = provider === 'telegram' ? 'getUpdates' : 'websocket_connect';
  return transcript.filter((entry) => entry.provider === provider && entry.operation === operation).reduce((total, entry) => total + (entry.repeatCount ?? 1), 0);
}

export function providerMessage(provider: Provider, destination: string, prompt: string, eventId: string) {
  if (provider === 'telegram') {
    return {
      provider,
      kind: 'message' as const,
      actorReference: destination,
      conversationReference: destination,
      payload: { message: { message_id: 1, date: 0, chat: { id: Number(destination), type: 'private' }, from: { id: Number(destination), is_bot: false, first_name: 'Acceptance' }, text: prompt } },
    };
  }
  if (provider === 'slack') {
    return {
      provider,
      kind: 'message' as const,
      actorReference: 'U1',
      conversationReference: destination,
      payload: { type: 'event_callback', event_id: eventId, event: { type: 'message', user: 'U1', channel: destination, text: prompt, ts: '1.000001' } },
    };
  }
  return {
    provider,
    kind: 'message' as const,
    actorReference: 'U1',
    conversationReference: destination,
    payload: { _simulatorDispatchType: 'MESSAGE_CREATE', id: eventId, channel_id: destination, author: { id: 'U1', bot: false }, content: prompt },
  };
}

/** Transcript assertions belong to the single provider-event vocabulary owner. */
export function hasProviderOperation(transcript: unknown, provider: Provider): boolean {
  return Array.isArray(transcript) && transcript.some((entry) => typeof entry === 'object' && entry !== null && (entry as { provider?: unknown }).provider === provider);
}

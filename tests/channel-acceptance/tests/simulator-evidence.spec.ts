import { expect, test } from '@playwright/test';
import { compactSimulatorTranscript } from '../src/simulator-evidence.js';

test('compacts only consecutive identical safe polling projections and preserves exact counts', () => {
  const transcript = compactSimulatorTranscript([
    { direction: 'outbound', provider: 'telegram', operation: 'getUpdates', payloadSha256: 'A', payloadBytes: 2, actionTokens: [], transportMessageId: null },
    { direction: 'outbound', provider: 'telegram', operation: 'getUpdates', payloadSha256: 'A', payloadBytes: 2, actionTokens: [], transportMessageId: null },
    { direction: 'outbound', provider: 'telegram', operation: 'sendMessage', payloadSha256: 'B', payloadBytes: 3, actionTokens: ['hitl:approve'], transportMessageId: '42' },
    { direction: 'outbound', provider: 'telegram', operation: 'getUpdates', payloadSha256: 'A', payloadBytes: 2, actionTokens: [], transportMessageId: null },
  ]);
  expect(transcript).toEqual([
    expect.objectContaining({ operation: 'getUpdates', payloadSha256: 'A', repeatCount: 2 }),
    expect.objectContaining({ operation: 'sendMessage', actionTokens: ['hitl:approve'], transportMessageId: '42', repeatCount: 1 }),
    expect.objectContaining({ operation: 'getUpdates', payloadSha256: 'A', repeatCount: 1 }),
  ]);
});

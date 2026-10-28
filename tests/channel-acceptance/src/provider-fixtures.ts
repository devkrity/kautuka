import type { Provider } from './model.js';

export function providerDestination(provider: Provider): string {
  return provider === 'slack' ? 'C100001' : '100001';
}

export function scenarioProviderDestination(provider: Provider, scenarioId: string): string {
  let hash = 0;
  for (const character of scenarioId) hash = (Math.imul(hash, 31) + character.charCodeAt(0)) >>> 0;
  const digits = String(100000 + (hash % 800000));
  return provider === 'slack' ? `C${digits}` : digits;
}

export function providerBotToken(provider: Provider): string {
  return provider === 'telegram' ? '123456789:abcdefghijklmnopqrstuv' : 'xoxb-acceptance-simulator';
}

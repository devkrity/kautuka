import { describe, expect, it } from 'vitest';
import { needsAuthorityCatchUp } from '../features/chat/kautuka/kautukaNaradIntentStatus';

describe('needsAuthorityCatchUp', () => {
  it('catches up when the accepted journal cursor is ahead of the live fold', () => {
    expect(needsAuthorityCatchUp(0, 2)).toBe(true);
    expect(needsAuthorityCatchUp(5, 7)).toBe(true);
  });

  it('does not catch up when live already holds the accepted cursor', () => {
    expect(needsAuthorityCatchUp(2, 2)).toBe(false);
    expect(needsAuthorityCatchUp(8, 7)).toBe(false);
  });

  it('does not invent a catch-up when accept omitted the journal cursor', () => {
    expect(needsAuthorityCatchUp(0, undefined)).toBe(false);
  });
});

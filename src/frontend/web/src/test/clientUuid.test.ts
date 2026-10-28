import { describe, expect, it } from 'vitest';
import { createClientUuid } from '@/lib/utils';

describe('createClientUuid', () => {
  it('uses randomUUID when available', () => {
    const value = createClientUuid({
      randomUUID: () => '11111111-2222-4333-8444-555555555555',
    });

    expect(value).toBe('11111111-2222-4333-8444-555555555555');
  });

  it('falls back to getRandomValues when randomUUID is unavailable', () => {
    const value = createClientUuid({
      getRandomValues: (array) => {
        array.set([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]);
        return array;
      },
    });

    expect(value).toBe('00010203-0405-4607-8809-0a0b0c0d0e0f');
  });
});
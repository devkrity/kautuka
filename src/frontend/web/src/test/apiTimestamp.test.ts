import { parseApiTimestamp } from '@/lib/utils';
import { describe, expect, it } from 'vitest';

describe('parseApiTimestamp', () => {
  it('accepts canonical timestamps with an explicit UTC offset', () => {
    expect(parseApiTimestamp('2026-07-15T00:00:00+10:00').toISOString()).toBe(
      '2026-07-14T14:00:00.000Z'
    );
  });

  it('rejects timestamps without an offset', () => {
    expect(parseApiTimestamp('2026-07-15T00:00:00').getTime()).toBe(0);
  });
});

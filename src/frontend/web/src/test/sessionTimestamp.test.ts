import { formatSessionTimestamp, formatTime } from '@/features/chat/utils/sessionTimestamp';
import { describe, expect, it } from 'vitest';

describe('session timestamp formatting', () => {
  it('uses an explicit English month and 24-hour time under a fixed timezone', () => {
    const originalTimeZone = process.env.TZ;
    process.env.TZ = 'UTC';
    try {
      expect(formatSessionTimestamp('2026-08-05T00:07:00.000Z')).toBe('05-Aug-26 00:07');
      expect(formatTime('2026-08-05T13:42:00.000Z')).toBe('13:42');
    } finally {
      if (originalTimeZone === undefined) delete process.env.TZ;
      else process.env.TZ = originalTimeZone;
    }
  });
});

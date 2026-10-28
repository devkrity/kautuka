import { describe, expect, it } from 'vitest';
import { computeRemaining } from '@/features/approvals/hooks/useApprovalsPage';

describe('approval remaining time', () => {
  it('uses singular and plural day labels', () => {
    expect(computeRemaining(new Date(Date.now() + 30 * 60 * 60 * 1000).toISOString())).toBe(
      '1 day left'
    );
    expect(computeRemaining(new Date(Date.now() + 50 * 60 * 60 * 1000).toISOString())).toBe(
      '2 days left'
    );
  });

  it('shows days for long review windows', () => {
    expect(computeRemaining(new Date(Date.now() + 1429 * 60 * 60 * 1000).toISOString())).toBe(
      '59 days left'
    );
  });
});

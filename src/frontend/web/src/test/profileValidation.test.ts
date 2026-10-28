import { describe, expect, it } from 'vitest';
import {
  defaultDisplayName,
  validateProfileDraft,
} from '@/features/auth/components/profileValidation';

describe('profile validation', () => {
  it('requires a display name and reports malformed email specifically', () => {
    expect(validateProfileDraft(' ', 'not-an-email')).toEqual({
      displayName: 'Display name is required.',
      email: 'Enter a valid email address.',
    });
  });

  it('defaults an unset display name to the username', () => {
    expect(defaultDisplayName(null, 'alex')).toBe('alex');
  });
});

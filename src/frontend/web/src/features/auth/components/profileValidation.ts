export type ProfileFieldErrors = { displayName?: string; email?: string };

export function validateProfileDraft(displayName: string, email: string): ProfileFieldErrors {
  const errors: ProfileFieldErrors = {};
  if (!displayName.trim()) errors.displayName = 'Display name is required.';
  const normalizedEmail = email.trim();
  if (normalizedEmail && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(normalizedEmail)) {
    errors.email = 'Enter a valid email address.';
  }
  return errors;
}

export function defaultDisplayName(
  displayName: string | null | undefined,
  username: string
): string {
  return displayName?.trim() || username;
}

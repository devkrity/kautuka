export interface OnboardingHandoffState {
  onboardingHandoffToken: string;
}

let activeHandoffToken: string | null = null;

/**
 * Allows the wizard to hand off to the dashboard without racing its final
 * provider-status refetch. The token intentionally lives only in this loaded
 * UI process: router history survives reloads, but an onboarding bypass must not.
 */
export function createOnboardingHandoffState(): OnboardingHandoffState {
  activeHandoffToken = globalThis.crypto.randomUUID();
  return { onboardingHandoffToken: activeHandoffToken };
}

export function isActiveOnboardingHandoff(state: unknown): boolean {
  if (!state || typeof state !== 'object') {
    return false;
  }

  const token = (state as Partial<OnboardingHandoffState>).onboardingHandoffToken;
  return typeof token === 'string' && token === activeHandoffToken;
}

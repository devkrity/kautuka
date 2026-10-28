export function isNaradIntentAccepted(status: string): boolean {
  return status === 'accepted' || status === 'already_applied';
}

/** Live tail is behind the journal cursor the intent just committed. */
export function needsAuthorityCatchUp(
  localSessionSequence: number,
  acceptedSessionSequence: number | undefined,
): boolean {
  return typeof acceptedSessionSequence === 'number' && acceptedSessionSequence > localSessionSequence;
}

export function formatNaradIntentFailure(response: Readonly<{ status: string; detail?: string }>): string {
  return response.detail ?? `Request ${response.status}`;
}

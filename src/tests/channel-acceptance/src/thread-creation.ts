/** Reads the sole correlation field needed from the create-thread response. */
export function readCreatedThreadId(payload: unknown): string {
  const id = payload && typeof payload === 'object' && !Array.isArray(payload)
    ? (payload as Record<string, unknown>).id
    : undefined;
  if (typeof id !== 'string' || !id.trim()) throw new Error('Create-thread response did not contain a thread id.');
  return id;
}

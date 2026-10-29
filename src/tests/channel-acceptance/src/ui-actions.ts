const actionTimeoutMs = 15_000;

/** Adds a bounded, named failure boundary around real browser interaction. */
export async function runBoundedUiAction(label: string, action: (timeout: number) => Promise<void>): Promise<void> {
  try {
    await action(actionTimeoutMs);
  } catch (error) {
    throw new Error(`UI action '${label}' failed within ${actionTimeoutMs}ms: ${error instanceof Error ? error.message : String(error)}`, { cause: error });
  }
}

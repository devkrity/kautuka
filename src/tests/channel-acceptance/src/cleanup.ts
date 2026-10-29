/** Runs one cleanup step without allowing it to mask the scenario outcome. */
export async function attemptCleanup(label: string, cleanup: () => Promise<void>): Promise<string | undefined> {
  try {
    await cleanup();
    return undefined;
  } catch (error) {
    return `${label} cleanup failed: ${error instanceof Error ? error.message : String(error)}`;
  }
}

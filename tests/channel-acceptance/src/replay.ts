/**
 * The evidence bundle records this command verbatim. It contains no secret,
 * port, endpoint, or local state path; the normal owned-stack executor owns
 * those values.
 */
export function replayCommand(scenarioId: string, seed: string): string {
  if (!/^CRH-\d{3}-[A-Z0-9-]+$/.test(scenarioId)) throw new Error(`Scenario id ${scenarioId} is not replay-safe.`);
  if (!/^[A-Za-z0-9._-]{1,128}$/.test(seed)) throw new Error('Acceptance seed must be a safe opaque identifier.');
  return `KAUTUKA_ACCEPTANCE_SEED=${seed} pnpm --dir tests/channel-acceptance exec playwright test --grep ${scenarioId}`;
}

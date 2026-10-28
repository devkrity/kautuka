import type { Provider, ProviderFault } from './model.js';

/**
 * A bounded, deterministic resilience execution. Quantities are supplied by
 * the caller so the product owner, rather than this harness, owns load and
 * soak policy. The profile name only selects the failure mix.
 */
export type ResilienceProfileName = 'pull-request' | 'nightly' | 'release-candidate';

export interface ResilienceProfile {
  readonly name: ResilienceProfileName;
  readonly iterations: number;
  readonly concurrency: number;
  readonly maximumDurationMs: number;
  readonly failureMix: readonly ResilienceFailure[];
}

export type ResilienceFailure =
  | { readonly boundary: 'gateway-process' | 'channel-host-process' | 'provider-simulator-process' | 'browser-session'; readonly action: 'restart' }
  | { readonly boundary: 'provider-http' | 'provider-websocket'; readonly action: 'partition'; readonly provider: Provider; readonly fault: ProviderFault['kind'] }
  | { readonly boundary: 'gateway-channelhost-mtls' | 'browser-sse' | 'service-dependency'; readonly action: 'partition' };

const profileFailures: Readonly<Record<ResilienceProfileName, readonly ResilienceFailure[]>> = {
  'pull-request': [
    { boundary: 'gateway-process', action: 'restart' },
    { boundary: 'provider-http', action: 'partition', provider: 'telegram', fault: 'transient-failure' },
    { boundary: 'browser-sse', action: 'partition' },
  ],
  nightly: [
    { boundary: 'gateway-process', action: 'restart' },
    { boundary: 'channel-host-process', action: 'restart' },
    { boundary: 'provider-simulator-process', action: 'restart' },
    { boundary: 'provider-http', action: 'partition', provider: 'slack', fault: 'rate-limit' },
    { boundary: 'provider-websocket', action: 'partition', provider: 'slack', fault: 'disconnect' },
    { boundary: 'gateway-channelhost-mtls', action: 'partition' },
    { boundary: 'browser-sse', action: 'partition' },
    { boundary: 'service-dependency', action: 'partition' },
  ],
  'release-candidate': [
    { boundary: 'gateway-process', action: 'restart' },
    { boundary: 'channel-host-process', action: 'restart' },
    { boundary: 'provider-simulator-process', action: 'restart' },
    { boundary: 'provider-http', action: 'partition', provider: 'telegram', fault: 'timeout' },
    { boundary: 'provider-http', action: 'partition', provider: 'slack', fault: 'rate-limit' },
    { boundary: 'provider-websocket', action: 'partition', provider: 'slack', fault: 'disconnect' },
    { boundary: 'gateway-channelhost-mtls', action: 'partition' },
    { boundary: 'browser-sse', action: 'partition' },
    { boundary: 'service-dependency', action: 'partition' },
  ],
};

/** Reads explicit policy values; it deliberately has no covert default load. */
export function resilienceProfileFromEnvironment(environment: NodeJS.ProcessEnv = process.env): ResilienceProfile {
  const name = parseName(environment.KAUTUKA_RESILIENCE_PROFILE);
  return {
    name,
    iterations: parsePositiveInteger(environment.KAUTUKA_RESILIENCE_ITERATIONS, 'KAUTUKA_RESILIENCE_ITERATIONS'),
    concurrency: parsePositiveInteger(environment.KAUTUKA_RESILIENCE_CONCURRENCY, 'KAUTUKA_RESILIENCE_CONCURRENCY'),
    maximumDurationMs: parsePositiveInteger(environment.KAUTUKA_RESILIENCE_MAX_DURATION_MS, 'KAUTUKA_RESILIENCE_MAX_DURATION_MS'),
    failureMix: profileFailures[name],
  };
}

/** Stable index ordering makes a failed matrix cell independently replayable. */
export function resilienceCaseId(profile: ResilienceProfile, iteration: number, failureIndex: number): string {
  if (!Number.isInteger(iteration) || iteration < 0 || iteration >= profile.iterations) throw new Error(`Iteration ${iteration} is outside ${profile.name}.`);
  if (!Number.isInteger(failureIndex) || failureIndex < 0 || failureIndex >= profile.failureMix.length) throw new Error(`Failure ${failureIndex} is outside ${profile.name}.`);
  return `${profile.name}:i${iteration}:f${failureIndex}`;
}

function parseName(value: string | undefined): ResilienceProfileName {
  if (value === 'pull-request' || value === 'nightly' || value === 'release-candidate') return value;
  throw new Error('KAUTUKA_RESILIENCE_PROFILE must be pull-request, nightly, or release-candidate.');
}

function parsePositiveInteger(value: string | undefined, name: string): number {
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed) || parsed <= 0) throw new Error(`${name} must be a positive integer supplied by the invoking policy.`);
  return parsed;
}

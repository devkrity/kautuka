import type { ProcessSpec, StackConfiguration } from './lifecycle-types.js';

export function validateOwnedConfiguration(configuration: StackConfiguration, services: readonly ProcessSpec[]): void {
  if ((!configuration.stack && services.length === 0) || !configuration.simulator) {
    throw new Error('Owned acceptance execution requires explicit stack and simulator process specifications. Reuse is opt-in only.');
  }
  if (configuration.stack && services.length > 0) {
    throw new Error('Owned acceptance execution must use either an aggregate stack process or direct service children, not both.');
  }
  const specs = [configuration.simulator, configuration.stack, ...services].filter((value): value is ProcessSpec => value !== undefined);
  const names = new Set<string>();
  for (const spec of specs) {
    if (!spec.name.trim()) throw new Error('Owned process names must not be empty.');
    if (!names.add(spec.name)) throw new Error(`Owned process name ${spec.name} is duplicated.`);
  }
  for (const service of services) {
    if (!service.readinessUrl && !service.readinessPort) {
      throw new Error(`Direct service child ${service.name} requires an exact readinessUrl or readinessPort.`);
    }
  }
}

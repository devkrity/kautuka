import { isAbsolute, normalize } from 'node:path';
import { expect, test } from '@playwright/test';
import { createResilienceExecution } from '../src/resilience-executor.js';

test('resilience execution owns direct service children with isolated state and exact readiness', () => {
  const previousSimulator = process.env.KAUTUKA_ACCEPTANCE_SIMULATOR_BASE_URI;
  const previousWeb = process.env.KAUTUKA_ACCEPTANCE_WEB_BASE_URI;
  const previousGateway = process.env.KAUTUKA_ACCEPTANCE_GATEWAY_BASE_URI;

  process.env.KAUTUKA_ACCEPTANCE_SIMULATOR_BASE_URI = 'http://127.0.0.1:18080/';
  process.env.KAUTUKA_ACCEPTANCE_WEB_BASE_URI = 'http://127.0.0.1:20229/';
  process.env.KAUTUKA_ACCEPTANCE_GATEWAY_BASE_URI = 'http://127.0.0.1:20000/';

  try {
    const execution = createResilienceExecution();
    const services = execution.stack.services;

    expect(execution.stack.stack).toBeUndefined();
    expect(normalize(execution.stateRoot)).toContain(normalize('.artifacts/channel-acceptance/resilience-state'));
    expect(execution.stack.simulator).toMatchObject({
      name: 'ProviderSimulator',
      command: 'dotnet',
      readinessUrl: new URL('http://127.0.0.1:18080/health'),
    });
    expect(execution.stack.simulator?.args).toHaveLength(1);
    expectDirectDll(execution.stack.simulator?.args[0], 'Kautuka.ProviderSimulator.dll');

    expect(services.map(({ name }) => name)).toEqual([
      'CredentialsHost',
      'ChannelHost',
      'Manager',
      'UpdateService',
      'Gateway',
      'WebHost',
    ]);
    expect(services.map(({ readinessPort, readinessUrl }) => readinessPort ?? readinessUrl?.toString())).toEqual([
      20002,
      20003,
      20001,
      20004,
      'http://127.0.0.1:20000/health',
      'http://127.0.0.1:20229/health',
    ]);

    for (const service of services) {
      expect(service.command).toBe('dotnet');
      expect(service.args).toHaveLength(1);
      expectDirectDll(service.args[0], `Kautuka.${service.name}.dll`);
      expect(service.environment).toMatchObject({
        Kautuka__ContainerStateRoot: execution.stateRoot,
        Kautuka__Channels__TransportProfile: 'Simulator',
        Kautuka__Channels__SimulatorBaseAddress: 'http://127.0.0.1:18080/',
      });
    }
  } finally {
    restore('KAUTUKA_ACCEPTANCE_SIMULATOR_BASE_URI', previousSimulator);
    restore('KAUTUKA_ACCEPTANCE_WEB_BASE_URI', previousWeb);
    restore('KAUTUKA_ACCEPTANCE_GATEWAY_BASE_URI', previousGateway);
  }
});

function expectDirectDll(path: string | undefined, fileName: string): void {
  expect(path).toBeDefined();
  expect(isAbsolute(path!)).toBe(true);
  expect(normalize(path!)).toContain(normalize(`bin/Debug/net10.0/${fileName}`));
}

function restore(name: string, value: string | undefined): void {
  if (value === undefined) {
    delete process.env[name];
    return;
  }

  process.env[name] = value;
}

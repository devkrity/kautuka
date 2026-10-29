import { expect, test } from '@playwright/test';
import { createAcceptanceExecution } from '../src/executor.js';

test('owned channel acceptance builds the current source before exercising provider provisioning', () => {
  const previousSimulator = process.env.KAUTUKA_ACCEPTANCE_SIMULATOR_BASE_URI;
  const previousWeb = process.env.KAUTUKA_ACCEPTANCE_WEB_BASE_URI;

  process.env.KAUTUKA_ACCEPTANCE_SIMULATOR_BASE_URI = 'http://127.0.0.1:18080/';
  process.env.KAUTUKA_ACCEPTANCE_WEB_BASE_URI = 'http://127.0.0.1:20229/';

  try {
    const execution = createAcceptanceExecution();
    expect(execution.stack.preflight?.map((step) => [step.name, step.command, step.args])).toEqual([
      ['GatewayBuild', 'dotnet', ['build', 'src/backend/Kautuka.Gateway/Kautuka.Gateway.csproj', '-c', 'Debug']],
      ['BackendBuild', 'dotnet', ['build', 'src/backend/Kautuka.WebHost/Kautuka.WebHost.csproj', '-c', 'Debug']],
      ['FrontendBuild', 'node', ['./node_modules/vite/bin/vite.js', 'build']],
    ]);
    expect(execution.stack.stack?.args).toEqual(['scripts/launch-kautuka.mjs', '--skip-build']);
    // Six sequential launcher stages at two minutes each, plus bounded process-start margin.
    expect(execution.stack.readinessTimeoutMs).toBe(750_000);
    expect(execution.stack.stack?.environment).toMatchObject({
      Kautuka__Channels__TransportProfile: 'Simulator',
      Kautuka__Channels__SimulatorBaseAddress: 'http://127.0.0.1:18080/',
    });
    expect(execution.allowedBrowserFindings?.some((pattern) => pattern.test('GET /changes net::ERR_ABORTED'))).toBeTruthy();
  } finally {
    restore('KAUTUKA_ACCEPTANCE_SIMULATOR_BASE_URI', previousSimulator);
    restore('KAUTUKA_ACCEPTANCE_WEB_BASE_URI', previousWeb);
  }
});

function restore(name: string, value: string | undefined): void {
  if (value === undefined) {
    delete process.env[name];
    return;
  }

  process.env[name] = value;
}

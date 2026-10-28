import { randomBytes, randomUUID } from 'node:crypto';
import { resolve } from 'node:path';
import { acceptancePathWithPodman } from './executor.js';
import type { AcceptanceExecution } from './harness.js';
import type { ProcessSpec, StackConfiguration } from './lifecycle.js';
import { HttpProviderSimulatorControl } from './simulator-control.js';

export interface ResilienceAcceptanceExecution extends AcceptanceExecution {
  readonly stack: StackConfiguration & { readonly services: readonly ProcessSpec[] };
  readonly gatewayBaseUrl: URL;
  readonly stateRoot: string;
}

/** Option A: Playwright owns every restartable service as a direct child. */
export function createResilienceExecution(): ResilienceAcceptanceExecution {
  const simulatorBaseUrl = requiredLoopbackUrl('KAUTUKA_ACCEPTANCE_SIMULATOR_BASE_URI', 'http://127.0.0.1:18080/');
  const baseUrl = requiredLoopbackUrl('KAUTUKA_ACCEPTANCE_WEB_BASE_URI', 'http://127.0.0.1:20229/');
  const gatewayBaseUrl = requiredLoopbackUrl('KAUTUKA_ACCEPTANCE_GATEWAY_BASE_URI', 'http://127.0.0.1:20000/');
  const root = resolve(process.cwd(), '../..');
  const executionId = randomUUID();
  const controlKey = randomBytes(32).toString('base64url');
  const stateRoot = resolve(root, '.artifacts/channel-acceptance/resilience-state', executionId);
  const simulatorStateFile = resolve(stateRoot, 'provider-simulator.json');
  const commonEnvironment = {
    ASPNETCORE_ENVIRONMENT: 'Development',
    Kautuka__ContainerStateRoot: stateRoot,
    Kautuka__Channels__TransportProfile: 'Simulator',
    Kautuka__Channels__SimulatorBaseAddress: simulatorBaseUrl.toString(),
    Path: acceptancePathWithPodman(),
  };
  const service = (name: string, projectDirectory: string, readiness: URL | number): ProcessSpec => ({
    name,
    command: 'dotnet',
    args: [resolve(root, projectDirectory, `bin/Debug/net10.0/Kautuka.${name}.dll`)],
    cwd: resolve(root, projectDirectory),
    environment: commonEnvironment,
    ...(readiness instanceof URL ? { readinessUrl: readiness } : { readinessPort: readiness }),
  });
  const services = [
    service('CredentialsHost', 'src/backend/Kautuka.CredentialsHost', 20002),
    service('ChannelHost', 'src/backend/Kautuka.ChannelHost', 20003),
    service('Manager', 'src/backend/Kautuka.Manager', 20001),
    service('UpdateService', 'src/backend/Kautuka.UpdateService', 20004),
    service('Gateway', 'src/backend/Kautuka.Gateway', new URL('/health', gatewayBaseUrl)),
    service('WebHost', 'src/backend/Kautuka.WebHost', new URL('/health', baseUrl)),
  ];

  return {
    stack: {
      baseUrl,
      readinessUrls: [new URL('/health', simulatorBaseUrl), new URL('/health', gatewayBaseUrl), new URL('/health', baseUrl)],
      reuseExistingStack: false,
      workspaceRoot: resolve(root, '.artifacts/channel-acceptance'),
      preflight: [
        { name: 'BackendBuild', command: 'dotnet', args: ['build', 'src/Kautuka.slnx', '-c', 'Debug', '--no-restore'], cwd: root },
        { name: 'FrontendBuild', command: 'node', args: ['./node_modules/vite/bin/vite.js', 'build'], cwd: resolve(root, 'src/frontend/web') },
      ],
      simulator: {
        name: 'ProviderSimulator',
        command: 'dotnet',
        args: [resolve(root, 'src/tools/Kautuka.ProviderSimulator/bin/Debug/net10.0/Kautuka.ProviderSimulator.dll')],
        cwd: resolve(root, 'src/tools/Kautuka.ProviderSimulator'),
        environment: {
          ProviderSimulator__ControlKey: controlKey,
          ProviderSimulator__Listen: simulatorBaseUrl.toString(),
          ProviderSimulator__StateFile: simulatorStateFile,
        },
        readinessUrl: new URL('/health', simulatorBaseUrl),
      },
      services,
    },
    simulator: new HttpProviderSimulatorControl(simulatorBaseUrl, controlKey),
    evidenceDirectory: resolve(root, 'artifacts/channel-acceptance'),
    seed: process.env.KAUTUKA_ACCEPTANCE_SEED ?? 'acceptance-seed',
    providerMode: 'simulator',
    allowedBrowserFindings: [/net::ERR_ABORTED$/],
    credentials: { username: process.env.KAUTUKA_ACCEPTANCE_USERNAME ?? 'admin', password: process.env.KAUTUKA_ACCEPTANCE_PASSWORD ?? 'passw0rd' },
    configurationForDigest: { simulator: simulatorBaseUrl.toString(), gateway: gatewayBaseUrl.toString(), baseUrl: baseUrl.toString(), mode: 'simulator-direct-children' },
    gatewayBaseUrl,
    stateRoot,
  };
}

function requiredLoopbackUrl(name: string, fallback?: string): URL {
  const value = process.env[name] ?? fallback;
  if (!value) throw new Error(`${name} is required.`);
  const url = new URL(value);
  if (!['127.0.0.1', '::1', 'localhost'].includes(url.hostname) || url.protocol !== 'http:') {
    throw new Error(`${name} must be an explicit HTTP loopback URI.`);
  }
  return url;
}

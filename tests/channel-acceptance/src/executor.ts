import { randomBytes } from 'node:crypto';
import { randomUUID } from 'node:crypto';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import type { AcceptanceExecution } from './harness.js';
import { HttpProviderSimulatorControl } from './simulator-control.js';
import { ownedWorkspacePath } from './workspace-safety.js';

// launch-kautuka starts six services sequentially and gives each service two minutes to
// bind its port. The outer lifecycle must cover that whole bounded graph, not one stage.
const aggregateStackReadinessTimeoutMs = (6 * 120_000) + 30_000;

/** Approved Development/Test-only concrete execution contract. */
export function createAcceptanceExecution(): AcceptanceExecution {
  const simulatorBaseUrl = requiredLoopbackUrl('KAUTUKA_ACCEPTANCE_SIMULATOR_BASE_URI', 'http://127.0.0.1:18080/');
  const baseUrl = requiredUrl('KAUTUKA_ACCEPTANCE_WEB_BASE_URI', 'http://127.0.0.1:20229/');
  const gatewayBaseUrl = requiredUrl('KAUTUKA_ACCEPTANCE_GATEWAY_BASE_URI', 'http://127.0.0.1:20000/');
  const root = resolve(process.cwd(), '../..');
  const workspaceRoot = resolve(root, '.artifacts/channel-acceptance');
  const workspace = createAcceptanceWorkspace(workspaceRoot, randomUUID());
  const controlKey = randomBytes(32).toString('base64url');
  const simulatorStateFile = resolve(workspace.stateRoot, 'provider-simulator.json');
  const commonEnvironment = {
    ASPNETCORE_ENVIRONMENT: 'Development',
    Kautuka__ContainerStateRoot: workspace.stateRoot,
    Kautuka__Channels__TransportProfile: 'Simulator',
    Kautuka__Channels__SimulatorBaseAddress: simulatorBaseUrl.toString(),
    Path: acceptancePathWithPodman(),
  };
  return {
    stack: {
      baseUrl,
      readinessUrls: [new URL('/health', simulatorBaseUrl), new URL('/health', gatewayBaseUrl), new URL('/health', baseUrl)],
      readinessTimeoutMs: aggregateStackReadinessTimeoutMs,
      reuseExistingStack: false,
      workspaceRoot,
      workspaceId: workspace.id,
      preflight: [
        // launch-kautuka runs every backend service with --no-build; Gateway must compile current Narad endpoints.
        { name: 'GatewayBuild', command: 'dotnet', args: ['build', 'src/backend/Kautuka.Gateway/Kautuka.Gateway.csproj', '-c', 'Debug'], cwd: root },
        { name: 'BackendBuild', command: 'dotnet', args: ['build', 'src/backend/Kautuka.WebHost/Kautuka.WebHost.csproj', '-c', 'Debug'], cwd: root },
        { name: 'FrontendBuild', command: 'node', args: ['./node_modules/vite/bin/vite.js', 'build'], cwd: resolve(root, 'src/frontend/web') },
      ],
      simulator: { name: 'ProviderSimulator', command: 'dotnet', args: ['run', '--project', 'src/tools/Kautuka.ProviderSimulator/Kautuka.ProviderSimulator.csproj'], cwd: root, environment: { ProviderSimulator__ControlKey: controlKey, ProviderSimulator__Listen: simulatorBaseUrl.toString(), ProviderSimulator__StateFile: simulatorStateFile } },
      // Acceptance owns a current-source stack. Reusing binaries here lets a passing
      // direct-worker test mask a missing service-graph registration.
      stack: { name: 'KautukaStack', command: 'node', args: ['scripts/launch-kautuka.mjs', '--skip-build'], cwd: root, environment: commonEnvironment },
    },
    simulator: new HttpProviderSimulatorControl(simulatorBaseUrl, controlKey),
    evidenceDirectory: resolve(root, 'artifacts/channel-acceptance'),
    seed: process.env.KAUTUKA_ACCEPTANCE_SEED ?? 'acceptance-seed',
    providerMode: 'simulator',
    allowedBrowserFindings: [/net::ERR_ABORTED$/],
    allowedServiceFindings: [
      /Workspace identity is not present in accepted desired state/i,
      /Telegram polling failed for registration/i,
      /Telegram getUpdates failed/i,
      /Workspace container reconcile loop failed/i,
      /trying to start process 'podman'/i,
      /Podman runtime not available or returned error/i,
      /Manager runtime adapter did not reach ready state during reconcile/i,
      /Hosted catalog refresh failed/i,
      /Manager update discovery request failed/i,
      /Workspace desired state push rejected during connector-save/i,
      /Failed to import built-in MCP bundle/i,
      /Manager rejected the desired state update/i,
      /Build MCPB runtime image for connector/i,
      /Channel authorization denied/i,
      /attachment_not_active/i,
      /Intent delivery failed/i,
      /MCP server process exited unexpectedly/i,
      /container state improper/i,
      /Built-in MCP metadata refresh failed/i,
      /Unexpected error probing connector/i,
      /remote party closed the WebSocket connection/i,
      /Slack connection loop error/i,
    ],
    credentials: { username: process.env.KAUTUKA_ACCEPTANCE_USERNAME ?? 'admin', password: process.env.KAUTUKA_ACCEPTANCE_PASSWORD ?? 'passw0rd' },
    configurationForDigest: { simulator: simulatorBaseUrl.toString(), gateway: gatewayBaseUrl.toString(), baseUrl: baseUrl.toString(), mode: 'simulator', state: 'owned-workspace' },
  };
}
export function createAcceptanceWorkspace(workspaceRoot: string, id: string): Readonly<{ id: string; stateRoot: string }> {
  return { id, stateRoot: resolve(ownedWorkspacePath(workspaceRoot, id), 'state') };
}
function requiredUrl(name: string, fallback?: string): URL { const value = process.env[name] ?? fallback; if (!value) throw new Error(`${name} is required.`); return new URL(value); }
function requiredLoopbackUrl(name: string, fallback?: string): URL { const url = requiredUrl(name, fallback); if (!['127.0.0.1', '::1', 'localhost'].includes(url.hostname) || url.protocol !== 'http:') throw new Error(`${name} must be an explicit HTTP loopback URI.`); return url; }

/** Manager looks up `podman` on PATH; Windows installs often omit it from non-interactive shells. */
export function acceptancePathWithPodman(): string {
  const current = process.env.Path ?? process.env.PATH ?? '';
  const candidates = [
    resolve(process.env.LOCALAPPDATA ?? '', 'Programs', 'Podman'),
    resolve(process.env.ProgramFiles ?? '', 'RedHat', 'Podman'),
    resolve(process.env.ProgramFiles ?? '', 'Podman'),
  ];
  const podmanDir = candidates.find((directory) => existsSync(resolve(directory, 'podman.exe')));
  if (!podmanDir || current.toLowerCase().includes(podmanDir.toLowerCase())) return current;
  return `${podmanDir};${current}`;
}

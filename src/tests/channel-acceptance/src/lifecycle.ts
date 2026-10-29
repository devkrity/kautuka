import { randomUUID } from 'node:crypto';
import { mkdir, rm } from 'node:fs/promises';
import type { OwnedProcessRecord, ProcessSpec, StackConfiguration } from './lifecycle-types.js';
import { OwnedChildProcesses } from './owned-child-processes.js';
import { runPreflight } from './preflight.js';
import { ownedWorkspacePath } from './workspace-safety.js';
import { validateOwnedConfiguration } from './lifecycle-validate.js';
import {
  assertEndpointUnoccupied,
  assertPortUnoccupied,
  waitForEndpointUnoccupied,
  waitForPort,
  waitForPortUnoccupied,
  waitForReady,
} from './readiness.js';

export type { OwnedProcessRecord, ProcessSpec, StackConfiguration } from './lifecycle-types.js';

export class OwnedStackLifecycle {
  private readonly processes: OwnedChildProcesses;
  readonly runId = randomUUID();
  readonly workspace: string;

  public constructor(private readonly configuration: StackConfiguration) {
    const workspaceId = configuration.workspaceId ?? this.runId;
    this.workspace = ownedWorkspacePath(configuration.workspaceRoot, workspaceId);
    this.processes = new OwnedChildProcesses(this.workspace);
  }

  public async start(): Promise<void> {
    await mkdir(this.workspace, { recursive: true });
    if (!this.configuration.reuseExistingStack) await this.startOwnedStack();
    await Promise.all(this.configuration.readinessUrls.map((url) =>
      waitForReady(url, () => this.processes.exitCodeForReadiness(url), this.configuration.readinessTimeoutMs)));
    await this.processes.writeEvidence();
  }

  /** Restarts only a named child spawned and still owned by this lifecycle instance. */
  public async restartOwnedProcess(name: string): Promise<OwnedProcessRecord> {
    const paused = await this.pauseOwnedProcess(name);
    return this.resumeOwnedProcess(paused.spec, paused.generation + 1);
  }

  /** Stops a direct child without deleting evidence; used for dependency-outage acceptance. */
  public async pauseOwnedProcess(name: string): Promise<Readonly<{ spec: ProcessSpec; generation: number }>> {
    if (this.configuration.reuseExistingStack) throw new Error('Cannot pause a process from a reused stack.');
    const owned = this.processes.find(name);
    if (!owned) throw new Error(`Process ${name} is not an owned child of acceptance run ${this.runId}.`);
    if (!owned.spec.readinessUrl && !owned.spec.readinessPort) {
      throw new Error(`Owned process ${name} has no exact readiness endpoint and cannot be independently paused.`);
    }
    const snapshot = { spec: owned.spec, generation: owned.generation };
    await this.processes.stopAndRemove(owned);
    if (owned.spec.readinessUrl) await waitForEndpointUnoccupied(owned.spec.readinessUrl);
    else await waitForPortUnoccupied(owned.spec.readinessPort!);
    await this.processes.writeEvidence();
    return snapshot;
  }

  /** Starts a previously paused direct child and waits for its readiness endpoint. */
  public async resumeOwnedProcess(spec: ProcessSpec, generation: number): Promise<OwnedProcessRecord> {
    if (this.configuration.reuseExistingStack) throw new Error('Cannot resume a process from a reused stack.');
    const record = await this.processes.start(spec, generation);
    await this.waitForProcessReady(spec);
    await this.processes.writeEvidence();
    return record;
  }

  public processRecords(): readonly OwnedProcessRecord[] { return this.processes.records(); }

  public async stop(): Promise<void> {
    await this.processes.stopAll();
    if (process.env.KAUTUKA_ACCEPTANCE_KEEP_WORKSPACE !== 'true') {
      await rm(this.workspace, { recursive: true, force: true, maxRetries: 3, retryDelay: 200 });
    }
  }

  public serviceFindings(): Array<{ service: string; level: 'warning' | 'error'; message: string }> {
    return this.processes.findings();
  }

  private async startOwnedStack(): Promise<void> {
    const services = this.configuration.services ?? [];
    validateOwnedConfiguration(this.configuration, services);
    for (const preflight of this.configuration.preflight ?? []) await runPreflight(preflight, this.workspace);
    await Promise.all(this.configuration.readinessUrls.map(assertEndpointUnoccupied));
    await Promise.all(services.flatMap((service) => service.readinessPort ? [assertPortUnoccupied(service.readinessPort)] : []));

    await this.processes.start(this.configuration.simulator!);
    await this.waitForProcessReady(this.configuration.simulator!);
    if (this.configuration.stack) await this.processes.start(this.configuration.stack);
    else for (const service of services) {
      await this.processes.start(service);
      await this.waitForProcessReady(service);
    }
  }

  private async waitForProcessReady(spec: ProcessSpec): Promise<void> {
    const exitCode = () => this.processes.find(spec.name)?.child.exitCode;
    if (spec.readinessUrl) await waitForReady(spec.readinessUrl, exitCode);
    else if (spec.readinessPort) await waitForPort(spec.readinessPort, exitCode);
  }
}

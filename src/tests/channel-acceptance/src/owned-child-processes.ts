import { spawn, type ChildProcess } from 'node:child_process';
import { appendFile, mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import type { OwnedProcessRecord, ProcessSpec } from './lifecycle-types.js';
import { parseServiceFindings } from './service-log-findings.js';

export interface OwnedChild {
  readonly spec: ProcessSpec;
  readonly child: ChildProcess;
  readonly generation: number;
}

export class OwnedChildProcesses {
  private readonly children: OwnedChild[] = [];
  private readonly history: OwnedProcessRecord[] = [];
  private readonly output = new Map<string, string>();

  public constructor(private readonly workspace: string) {}

  public async start(spec: ProcessSpec, generation = 1): Promise<OwnedProcessRecord> {
    await mkdir(this.workspace, { recursive: true });
    const child = spawn(spec.command, [...spec.args], {
      cwd: spec.cwd,
      env: { ...process.env, ...spec.environment },
      stdio: ['ignore', 'pipe', 'pipe'],
      detached: process.platform !== 'win32',
      windowsHide: true,
    });
    await new Promise<void>((resolveStarted, rejectStarted) => {
      child.once('spawn', resolveStarted);
      child.once('error', rejectStarted);
    });
    if (!child.pid) throw new Error(`Acceptance process ${spec.name} did not expose a PID.`);

    this.children.push({ spec, child, generation });
    const record = { name: spec.name, pid: child.pid, command: spec.command, generation, startedAtUtc: new Date().toISOString() };
    this.history.push(record);
    const capture = (chunk: Buffer): void => {
      const text = chunk.toString();
      this.output.set(spec.name, `${this.output.get(spec.name) ?? ''}${text}`.slice(-64_000));
      void appendFile(join(this.workspace, `${spec.name}.log`), text);
    };
    child.stdout?.on('data', capture);
    child.stderr?.on('data', capture);
    return record;
  }

  public find(name: string): OwnedChild | undefined {
    return this.children.find((entry) => entry.spec.name === name);
  }

  public exitCodeForReadiness(url: URL): number | null | undefined {
    return (this.children.find((entry) => entry.spec.readinessUrl?.origin === url.origin)
      ?? this.find('KautukaStack'))?.child.exitCode;
  }

  public async stopAndRemove(owned: OwnedChild): Promise<void> {
    await stopOwnedChild(owned.child);
    const index = this.children.indexOf(owned);
    if (index >= 0) this.children.splice(index, 1);
  }

  public async stopAll(): Promise<void> {
    const owned = [...this.children];
    await Promise.all(owned.reverse().map(({ child }) => stopOwnedChild(child)));
    const survivors = owned.filter(({ child }) => child.exitCode === null);
    if (survivors.length > 0) throw new Error(`Owned acceptance processes survived cleanup: ${survivors.map(({ spec, child }) => `${spec.name} (${child.pid ?? 'unknown'})`).join(', ')}.`);
    this.children.length = 0;
  }

  public records(): readonly OwnedProcessRecord[] { return [...this.history]; }

  public findings(): Array<{ service: string; level: 'warning' | 'error'; message: string }> {
    return [...this.output.entries()].flatMap(([service, output]) => parseServiceFindings(service, output));
  }

  public async writeEvidence(): Promise<void> {
    await writeFile(join(this.workspace, 'owned-processes.json'), JSON.stringify(this.history, null, 2));
  }
}

async function stopOwnedChild(child: ChildProcess): Promise<void> {
  if (!child.pid || child.exitCode !== null) return;
  if (process.platform === 'win32') {
    const exited = waitForChildExit(child);
    const taskkill = spawn('taskkill', ['/pid', String(child.pid), '/T', '/F'], { stdio: 'ignore', windowsHide: true });
    const result = await new Promise<number | null>((resolveStopped, rejectStopped) => {
      taskkill.once('error', rejectStopped);
      taskkill.once('exit', resolveStopped);
    });
    await exited;
    if (child.exitCode === null) {
      throw new Error(`taskkill failed for owned process ${child.pid} (exit ${result ?? 'unknown'}).`);
    }
    return;
  }
  child.kill('SIGTERM');
  await delay(1_000);
  if (child.exitCode === null) { child.kill('SIGKILL'); await waitForChildExit(child); }
}

async function waitForChildExit(child: ChildProcess): Promise<void> {
  if (child.exitCode !== null) return;
  await new Promise<void>((resolveExited) => child.once('exit', () => resolveExited()));
}

import { appendFile, mkdir } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { spawn } from 'node:child_process';
import type { ProcessSpec } from './lifecycle.js';

/** Runs finite build preparation before the acceptance readiness clock begins. */
export async function runPreflight(spec: ProcessSpec, workspace: string): Promise<void> {
  const log = join(workspace, `${spec.name}.log`);
  await mkdir(dirname(log), { recursive: true });
  await new Promise<void>((resolveCompleted, rejectCompleted) => {
    const child = spawn(spec.command, [...spec.args], { cwd: spec.cwd, env: { ...process.env, ...spec.environment }, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
    const capture = (chunk: Buffer) => { void appendFile(log, chunk); };
    child.stdout?.on('data', capture); child.stderr?.on('data', capture);
    child.once('error', rejectCompleted);
    child.once('exit', (code) => code === 0 ? resolveCompleted() : rejectCompleted(new Error(`Acceptance preflight ${spec.name} failed (exit ${code ?? 'null'}). See ${log}.`)));
  });
}

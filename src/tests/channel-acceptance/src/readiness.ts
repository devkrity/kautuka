import { createConnection } from 'node:net';
import { setTimeout as delay } from 'node:timers/promises';

export async function assertEndpointUnoccupied(url: URL): Promise<void> {
  if (!url.port) return;
  if (await isPortOccupied(Number(url.port), url.hostname)) {
    throw new Error(`Refusing to start an owned acceptance stack because ${url.origin} is already in use. Use a dedicated stack profile or explicitly configured reuse mode.`);
  }
}

export async function assertPortUnoccupied(port: number): Promise<void> {
  if (await isPortOccupied(port)) {
    throw new Error(`Refusing to start an owned acceptance process because loopback port ${port} is already in use.`);
  }
}

export async function waitForEndpointUnoccupied(url: URL, timeoutMs = 15_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      await assertEndpointUnoccupied(url);
      return;
    } catch {
      await delay(100);
    }
  }
  throw new Error(`Owned process endpoint ${url.origin} did not close after termination.`);
}

export async function waitForPortUnoccupied(port: number, timeoutMs = 15_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (!await isPortOccupied(port)) return;
    await delay(100);
  }
  throw new Error(`Owned process port ${port} did not close after termination.`);
}

export async function waitForPort(
  port: number,
  processExitCode: () => number | null | undefined,
  timeoutMs = 60_000,
): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    assertRunning(processExitCode(), `port ${port}`);
    if (await isPortOccupied(port)) return;
    await delay(200);
  }
  throw new Error(`Timed out waiting for owned process port ${port}.`);
}

export async function waitForReady(
  url: URL,
  processExitCode: () => number | null | undefined,
  timeoutMs = 60_000,
): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    assertRunning(processExitCode(), `readiness at ${url}`);
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(2_000) });
      if (response.ok) return;
    } catch { /* readiness is retried until the deadline */ }
    await delay(250);
  }
  throw new Error(`Timed out waiting for readiness at ${url}.`);
}

async function isPortOccupied(port: number, host = '127.0.0.1'): Promise<boolean> {
  return await new Promise<boolean>((resolveOccupied) => {
    const socket = createConnection({ host, port });
    socket.once('connect', () => { socket.destroy(); resolveOccupied(true); });
    socket.once('error', () => { socket.destroy(); resolveOccupied(false); });
  });
}

function assertRunning(exitCode: number | null | undefined, target: string): void {
  if (exitCode !== null && exitCode !== undefined) {
    throw new Error(`Owned process exited before ${target} (exit ${exitCode}).`);
  }
}

export interface ProcessSpec {
  readonly name: string;
  readonly command: string;
  readonly args: readonly string[];
  readonly cwd: string;
  readonly environment?: Readonly<Record<string, string>>;
  /** Exact endpoint owned by this child. Required when the child is independently restartable. */
  readonly readinessUrl?: URL;
  /** Exact loopback TCP port for mTLS/gRPC children that do not expose an HTTP health endpoint. */
  readonly readinessPort?: number;
}

export interface StackConfiguration {
  readonly baseUrl: URL;
  readonly readinessUrls: readonly URL[];
  /** Bounded startup allowance for the real service graph, including hosted-service readiness. */
  readonly readinessTimeoutMs?: number;
  readonly reuseExistingStack: boolean;
  readonly stack?: ProcessSpec;
  readonly simulator?: ProcessSpec;
  /** Resilience-only direct children. Mutually exclusive with the aggregate stack process. */
  readonly services?: readonly ProcessSpec[];
  /** Build artifacts required by the owned stack, completed before readiness timing starts. */
  readonly preflight?: readonly ProcessSpec[];
  readonly workspaceRoot: string;
  /** A single safe path segment, shared by the owned workspace and its runtime state. */
  readonly workspaceId?: string;
}

export interface OwnedProcessRecord {
  readonly name: string;
  readonly pid: number;
  readonly command: string;
  readonly generation: number;
  readonly startedAtUtc: string;
}

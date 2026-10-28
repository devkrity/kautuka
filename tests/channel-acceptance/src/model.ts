import type { Page } from '@playwright/test';
import type { AcceptanceFixtures } from './public-fixture-bootstrap.js';
import type { AcceptanceSkillFixtures } from './skill-fixture-bootstrap.js';

export const providers = ['telegram', 'slack'] as const;
export type Provider = (typeof providers)[number];

export const approvalActions = ['approve', 'deny', 'stop-run'] as const;
export type ApprovalAction = (typeof approvalActions)[number];

export type ScenarioTag = 'smoke' | 'regression' | 'full' | 'live-provider' | 'release-gate' | 'manual';
export type ScenarioStatus = 'passed' | 'failed' | 'skipped' | 'blocked';

export interface ScenarioContext {
  readonly page: Page;
  readonly baseUrl: URL;
  readonly simulator: ProviderSimulatorControl;
  readonly evidence: AcceptanceEvidence;
  readonly seed: string;
  readonly fixtures: AcceptanceFixtures;
  readonly skillFixtures: AcceptanceSkillFixtures;
}

export interface AcceptanceScenario {
  readonly id: string;
  readonly title: string;
  readonly tags: readonly ScenarioTag[];
  readonly providers?: readonly Provider[];
  readonly checklistIds: readonly string[];
  readonly execute: (context: ScenarioContext) => Promise<void>;
}

/**
 * This is the approved simulator boundary as consumed by acceptance tests.
 * The standalone simulator owns its HTTP/authentication contract; this suite
 * deliberately does not select paths, ports, credentials, or provider overrides.
 */
export interface ProviderSimulatorControl {
  reset(): Promise<void>;
  configure(provider: Provider, setup: ProviderSetup): Promise<void>;
  injectInbound(event: ProviderInboundEvent): Promise<void>;
  scheduleFault(fault: ProviderFault): Promise<void>;
  exportEvidence(): Promise<SimulatorEvidence>;
}

export interface ProviderSetup {
  readonly scenarioId: string;
  readonly seed: string;
  readonly ownerReference: string;
  readonly conversationReference?: string;
}

export interface ProviderInboundEvent {
  readonly provider: Provider;
  readonly kind: 'message' | 'command' | 'reaction' | 'approval' | 'file';
  readonly actorReference: string;
  readonly conversationReference: string;
  readonly payload: Readonly<Record<string, unknown>>;
}

export interface ProviderFault {
  readonly provider: Provider;
  readonly kind: 'disconnect' | 'latency' | 'dropped-ack' | 'duplicate' | 'rate-limit' | 'transient-failure' | 'permanent-failure' | 'timeout' | 'drop' | 'truncated' | 'uncertain_effect' | 'rate_limit' | 'transient';
  readonly at: 'before-ingress' | 'after-ingress' | 'before-outbound' | 'after-outbound';
}

export interface SimulatorEvidence {
  readonly providerMode: string;
  readonly transcript: readonly SimulatorTranscriptEntry[];
  readonly outboundEffects: unknown;
  /** Durable Telegram queue depth reported by the standalone simulator control plane. */
  readonly pendingTelegramUpdates?: number;
  readonly protocolVersions?: Readonly<Record<Provider, string>>;
}

export interface SimulatorTranscriptEntry {
  readonly direction?: string;
  readonly provider?: string;
  readonly operation?: string;
  readonly payloadSha256?: string;
  readonly payloadBytes?: number;
  readonly actionTokens?: readonly string[];
  readonly transportMessageId?: string | null;
  readonly repeatCount?: number;
}

export interface AcceptanceEvidence {
  addCanonicalRange(range: CanonicalRange): void;
  addIdentifier(identifier: EvidenceIdentifier): void;
  addLifecycleAssertion(assertion: LifecycleAssertion): void;
  addServiceFinding(finding: ServiceFinding): void;
  addConnectDiagnostic(diagnostic: ConnectDiagnostic): void;
  addSkipped(checklistId: string, reason: string): void;
}

export interface CanonicalRange { readonly firstEventId: string; readonly lastEventId: string; readonly firstSequence: number; readonly lastSequence: number; }
export interface EvidenceIdentifier { readonly kind: 'approval' | 'wait' | 'invocation' | 'run' | 'correlation' | 'event'; readonly value: string; }
export interface ServiceFinding { readonly service: string; readonly level: 'warning' | 'error'; readonly message: string; }
export interface ConnectDiagnostic { readonly timestampMs: number; readonly threadId?: string; readonly runId?: string; readonly resume?: Readonly<{ interruptId?: string; status?: string }>; readonly endpoint?: 'message' | 'resolve-interrupt'; }
export interface LifecycleAssertion { readonly runId: string; readonly outcome: 'terminal-once' | 'wait-resolved'; readonly eventIds: readonly string[]; }

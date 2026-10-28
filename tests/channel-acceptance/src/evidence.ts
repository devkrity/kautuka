import { createHash } from 'node:crypto';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import type { AcceptanceEvidence, CanonicalRange, ConnectDiagnostic, EvidenceIdentifier, LifecycleAssertion, Provider, ScenarioStatus, ServiceFinding, SimulatorEvidence } from './model.js';

const secretKey = /(?:authorization|cookie|credential|password|secret|token|api[-_]?key|private[-_]?key)/i;
const secretValue = /(?:bearer\s+|xox[baprs]-|\b\d{8,}:[A-Za-z0-9_-]{20,}|-----BEGIN [A-Z ]+-----)/i;

export class EvidenceBundle implements AcceptanceEvidence {
  private readonly canonicalRanges: CanonicalRange[] = [];
  private readonly identifiers: EvidenceIdentifier[] = [];
  private readonly lifecycleAssertions: LifecycleAssertion[] = [];
  private readonly serviceFindings: ServiceFinding[] = [];
  private readonly browserFindings: Array<{ kind: 'console' | 'page-error' | 'network'; message: string }> = [];
  private readonly skipped: Array<{ checklistId: string; reason: string }> = [];
  private readonly connectDiagnostics: ConnectDiagnostic[] = [];

  public constructor(private readonly metadata: { scenarioId: string; seed: string; replayCommand: string; providerMode: string; commit: string; configurationDigest: string; providers: readonly Provider[] }) {}
  public addCanonicalRange(range: CanonicalRange): void { this.canonicalRanges.push(range); }
  public addIdentifier(identifier: EvidenceIdentifier): void { this.identifiers.push(identifier); }
  public addLifecycleAssertion(assertion: LifecycleAssertion): void { this.lifecycleAssertions.push(assertion); }
  public addServiceFinding(finding: ServiceFinding): void { this.serviceFindings.push(finding); }
  public addConnectDiagnostic(diagnostic: ConnectDiagnostic): void { this.connectDiagnostics.push(diagnostic); }
  public addBrowserFinding(finding: { kind: 'console' | 'page-error' | 'network'; message: string }): void { this.browserFindings.push(finding); }
  public addSkipped(checklistId: string, reason: string): void { this.skipped.push({ checklistId, reason }); }

  public async write(outputDirectory: string, status: ScenarioStatus, simulator: SimulatorEvidence): Promise<void> {
    const evidence = redact({ ...this.metadata, status, canonicalRanges: this.canonicalRanges, identifiers: this.identifiers, lifecycleAssertions: this.lifecycleAssertions, serviceFindings: this.serviceFindings, connectDiagnostics: this.connectDiagnostics, browserFindings: this.browserFindings, skipped: this.skipped, simulator });
    const serialized = JSON.stringify(evidence, null, 2);
    assertNoSecretShapedEvidence(serialized);
    await mkdir(outputDirectory, { recursive: true });
    await writeFile(join(outputDirectory, `${this.metadata.scenarioId}.json`), serialized, 'utf8');
  }
}

export function configurationDigest(configuration: unknown): string { return createHash('sha256').update(JSON.stringify(redact(configuration))).digest('hex'); }

export function redact(value: unknown): unknown {
  if (typeof value === 'string') return secretValue.test(value) ? '[REDACTED]' : value;
  if (Array.isArray(value)) return value.map(redact);
  if (!value || typeof value !== 'object') return value;
  return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, secretKey.test(key) ? '[REDACTED]' : redact(item)]));
}

export function assertNoSecretShapedEvidence(serialized: string): void {
  if (secretValue.test(serialized)) throw new Error('Acceptance evidence contains a secret-shaped value.');
}

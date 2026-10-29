import { execFileSync } from 'node:child_process';
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import type { Browser } from '@playwright/test';
import { EvidenceBundle, configurationDigest, redact } from './evidence.js';
import { attemptCleanup } from './cleanup.js';
import { projectConnectDiagnostic, recordConnectDiagnostic } from './connect-diagnostics.js';
import { OwnedStackLifecycle, type StackConfiguration } from './lifecycle.js';
import type { AcceptanceScenario, ProviderSimulatorControl, ScenarioContext, ScenarioStatus } from './model.js';
import { replayCommand } from './replay.js';
import { authenticateAcceptanceUser } from './acceptance-login.js';
import { bootstrapPublicFixtures } from './public-fixture-bootstrap.js';
import { bootstrapSkillFixtures } from './skill-fixture-bootstrap.js';
import { observeNaradLifecycle } from './narad-operational-evidence.js';
export interface AcceptanceExecution {
  readonly stack: StackConfiguration;
  readonly simulator: ProviderSimulatorControl;
  readonly evidenceDirectory: string;
  readonly seed: string;
  readonly providerMode: string;
  readonly configurationForDigest: unknown;
  readonly allowedBrowserFindings?: readonly RegExp[];
  readonly allowedServiceFindings?: readonly RegExp[];
  readonly credentials: { readonly username: string; readonly password: string };
}
export async function executeScenario(browser: Browser, scenario: AcceptanceScenario, execution: AcceptanceExecution): Promise<void> {
  const lifecycle = execution.stack.reuseExistingStack ? undefined : new OwnedStackLifecycle(execution.stack);
  const evidence = new EvidenceBundle({
    scenarioId: scenario.id,
    seed: execution.seed,
    replayCommand: replayCommand(scenario.id, execution.seed),
    providerMode: execution.providerMode,
    commit: currentCommit(),
    configurationDigest: configurationDigest(execution.configurationForDigest),
    providers: scenario.providers ?? [],
  });
  let status: ScenarioStatus = 'failed';
  let context: import('@playwright/test').BrowserContext | undefined;
  let operationalEvidence: { complete(): Promise<void> } | undefined;
  const unexpectedBrowserFindings: string[] = [];
  let primaryFailure: unknown;
  let cleanupFailures: string[] = [];
  try {
    await lifecycle?.start();
    context = await browser.newContext();
    const page = await context.newPage();
    operationalEvidence = await observeNaradLifecycle(page, evidence);
    const recordBrowserFinding = (kind: 'console' | 'page-error' | 'network', message: string): void => {
      evidence.addBrowserFinding({ kind, message });
      if (!(execution.allowedBrowserFindings ?? []).some((pattern) => pattern.test(message))) unexpectedBrowserFindings.push(`${kind}: ${message}`);
    };
    page.on('console', (message) => {
      if (message.type() === 'warning' || message.type() === 'error') recordBrowserFinding('console', message.text());
    });
    page.on('pageerror', (error) => recordBrowserFinding('page-error', error.message));
    page.on('requestfailed', (request) => recordBrowserFinding('network', `${request.method()} ${request.url()} ${request.failure()?.errorText ?? 'failed'}`));
    page.on('request', (request) => {
      try {
        const diagnostic = projectConnectDiagnostic(request.method(), request.url(), request.postDataJSON());
        if (diagnostic) recordConnectDiagnostic(evidence, diagnostic);
      } catch { /* A malformed connect body contributes no diagnostic. */ }
    });
    page.on('response', (response) => {
      if (response.status() >= 400 && new URL(response.url()).pathname.startsWith('/api/')) {
        recordBrowserFinding('network', `${response.request().method()} ${new URL(response.url()).pathname} ${response.status()}`);
      }
    });
    const headers = await authenticateAcceptanceUser(page, execution.stack.baseUrl, execution.credentials);
    const fixtures = await bootstrapPublicFixtures(page, execution.stack.baseUrl, headers); const skillFixtures = await bootstrapSkillFixtures(page, execution.stack.baseUrl, fixtures, headers);
    await scenario.execute({ page, baseUrl: execution.stack.baseUrl, simulator: execution.simulator, evidence, seed: execution.seed, fixtures, skillFixtures });
    if (unexpectedBrowserFindings.length > 0) throw new Error(`Unexpected browser findings:\n${unexpectedBrowserFindings.join('\n')}`);
    status = 'passed';
  } catch (error) {
    primaryFailure = error;
    evidence.addServiceFinding({ service: 'acceptance-harness', level: 'error', message: error instanceof Error ? error.message : String(error) });
  } finally {
    let simulatorEvidence;
    try { simulatorEvidence = await execution.simulator.exportEvidence(); }
    catch (error) {
      evidence.addServiceFinding({ service: 'provider-simulator', level: 'error', message: `Evidence export failed: ${error instanceof Error ? error.message : String(error)}` });
      simulatorEvidence = { providerMode: execution.providerMode, transcript: [], outboundEffects: [] };
    }
    const closeFailure = await attemptCleanup('browser context', async () => { await context?.close(); });
    const operationalFailure = operationalEvidence ? await attemptCleanup('narad lifecycle evidence', () => operationalEvidence!.complete()) : undefined;
    const stopFailure = lifecycle ? await attemptCleanup('owned stack', () => lifecycle.stop()) : undefined;
    cleanupFailures = [closeFailure, operationalFailure, stopFailure].filter((failure): failure is string => Boolean(failure));
    if (unexpectedBrowserFindings.length > 0 && !primaryFailure) cleanupFailures.push(`Unexpected browser findings:\n${unexpectedBrowserFindings.join('\n')}`);
    const serviceFindings = lifecycle?.serviceFindings() ?? [];
    for (const finding of serviceFindings) evidence.addServiceFinding(finding);
    const unexpectedService = serviceFindings.filter((finding) => !(execution.allowedServiceFindings ?? []).some((pattern) => pattern.test(finding.message)));
    if (unexpectedService.length > 0) cleanupFailures.push(`Unexpected service findings:\n${unexpectedService.map((finding) => `${finding.level}: ${finding.message}`).join('\n')}`);
    for (const message of cleanupFailures) evidence.addServiceFinding({ service: 'acceptance-harness', level: 'error', message });
    if (cleanupFailures.length > 0) status = 'failed';
    const writeFailure = await attemptCleanup('evidence', () => evidence.write(join(execution.evidenceDirectory, 'evidence'), status, simulatorEvidence));
    if (writeFailure) cleanupFailures.push(writeFailure);
  }
  if (primaryFailure) throw primaryFailure;
  if (cleanupFailures.length > 0) throw new Error(`Scenario cleanup failed:\n${cleanupFailures.join('\n')}`);
}
function currentCommit(): string {
  try { return execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim(); }
  catch { return 'unknown'; }
}

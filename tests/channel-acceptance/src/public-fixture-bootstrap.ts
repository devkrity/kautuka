import { expect, type APIResponse, type Page } from '@playwright/test';

export interface AcceptanceFixtures {
  readonly providerId: string;
  readonly modelId: string;
  readonly assistantId: string;
  readonly rollerId: string;
}

export async function bootstrapPublicFixtures(page: Page, baseUrl: URL, headers: Record<string, string>): Promise<AcceptanceFixtures> {
  const fixtures: AcceptanceFixtures = {
    providerId: 'acceptance-ollama', modelId: 'acceptance-model',
    assistantId: 'acceptance-assistant', rollerId: 'acceptance-roller',
  };
  const configuration = fixtureConfiguration();
  const request = page.context().request;
  await requireSuccess(await request.post(endpoint(baseUrl, '/api/settings/providers/save'), {
    headers, data: { id: fixtures.providerId, type: 'ollama', settings: { base_url: configuration.providerBaseUrl } },
  }), 'create acceptance provider');
  await requireSuccess(await request.post(endpoint(baseUrl, '/api/settings/models/save'), {
    headers, data: { id: fixtures.modelId, providerId: fixtures.providerId, modelName: configuration.modelName, settings: {} },
  }), 'create acceptance model');
  for (const [id, name] of [[fixtures.assistantId, 'Assistant'], [fixtures.rollerId, 'Roller']] as const) {
    await requireSuccess(await request.put(endpoint(baseUrl, `/api/settings/agents/${id}`), {
      headers, data: agentPayload(name, fixtures.modelId),
    }), `create ${name} fixture`);
  }
  return fixtures;
}

export function fixtureConfiguration(environment: Readonly<Record<string, string | undefined>> = process.env): Readonly<{ providerBaseUrl: string; modelName: string }> {
  const modelName = environment.KAUTUKA_ACCEPTANCE_MODEL_NAME?.trim();
  if (!modelName) throw new Error('KAUTUKA_ACCEPTANCE_MODEL_NAME must name an installed acceptance model.');
  return {
    providerBaseUrl: environment.KAUTUKA_ACCEPTANCE_PROVIDER_BASE_URL ?? 'http://127.0.0.1:11434',
    modelName,
  };
}

function endpoint(baseUrl: URL, path: string): string { return new URL(path, baseUrl).toString(); }
function agentPayload(name: string, modelId: string) {
  return {
    name, modelId, systemPrompt: 'Acceptance fixture.', superExecMode: 'disabled', mcpBindings: [], skillAttachments: [],
    approvalConfig: { requireMcpIds: [], requireMcpFunctionFilters: {}, disabledMcpFunctions: {}, timeoutSeconds: 300 },
    toolExposure: { exposedAsTool: false, description: '', invocationTimeoutSeconds: 120 },
    session: { historyEnabled: true, maxHistoryRuns: 5, summariesEnabled: false, memoryFactExtractionEnabled: false },
    sandbox: { enabled: false, allowedLanguages: [], maxMemory: '2g', maxTimeout: 120 },
  };
}
async function requireSuccess(response: APIResponse, operation: string): Promise<void> {
  expect(response.ok(), `${operation} failed with ${response.status()}`).toBeTruthy();
}

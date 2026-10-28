import { expect, type APIRequestContext, type APIResponse, type Page } from '@playwright/test';
import { strToU8, zipSync } from 'fflate';

type PublicAgentFixtures = Readonly<{ assistantId: string; rollerId: string }>;
type AgentRecord = Record<string, unknown> & Readonly<{ id: string }>;

export type AcceptanceSkillFixtures = Readonly<{
  skillId: string;
  parentAgentId: string;
  childAgentId: string;
}>;

const skillId = 'roll-dice';
const instructions = `---
name: roll-dice
description: Deterministically roll a requested die for channel lifecycle acceptance.
---

# Roll Dice

Load this skill, then call run_skill_script with namedArguments containing sides.

\`\`\`bash
sides=<sides>
case "$sides" in (*[!0-9]*|'') echo "sides must be a positive integer" >&2; exit 2;; esac
if [ "$sides" -lt 1 ]; then echo "sides must be positive" >&2; exit 2; fi
echo $((sides / 2 + 1))
\`\`\`
`;

export async function bootstrapSkillFixtures(
  page: Page,
  baseUrl: URL,
  fixtures: PublicAgentFixtures,
  headers: Record<string, string>,
): Promise<AcceptanceSkillFixtures> {
  const request = page.context().request;
  const archive = zipSync({ [`${skillId}/SKILL.md`]: strToU8(instructions) }, { level: 6 });
  const imported = await request.post(new URL('/api/skills/import', baseUrl).toString(), {
    headers,
    multipart: {
      file: { name: `${skillId}.zip`, mimeType: 'application/zip', buffer: Buffer.from(archive) },
      sourceMetadata: JSON.stringify({ source: 'channel-acceptance-public-bootstrap' }),
    },
  });
  await requireSuccess(imported, 'import roll-dice skill');
  const importedSkillId = readImportedSkillId(await imported.json());

  const agentsResponse = await request.get(new URL('/api/settings/agents', baseUrl).toString(), { headers });
  await requireSuccess(agentsResponse, 'list acceptance agents');
  const payload = await agentsResponse.json() as { agents?: unknown };
  const agents = Array.isArray(payload.agents) ? payload.agents as AgentRecord[] : [];
  const roller = requireAgent(agents, fixtures.rollerId);
  const assistant = requireAgent(agents, fixtures.assistantId);

  await saveAgent(request, baseUrl, headers, roller, {
    systemPrompt: 'You are Roller. Always use load_skill("roll-dice") before rolling. Never calculate a die yourself. For every die named by the user, call run_skill_script once with that die as sides, one call at a time. If the user names two dice, make exactly two sequential run_skill_script calls and only then summarize both results. Never finish after a single roll when two dice were requested.',
    sandbox: { ...asRecord(roller.sandbox), enabled: true },
    skillAttachments: [{ skillId: importedSkillId, mode: 'requireApproval' }],
    toolExposure: {
      ...asRecord(roller.toolExposure),
      exposedAsTool: true,
      description: 'Roll a die with the deterministic roll-dice skill.',
    },
  });
  await saveAgent(request, baseUrl, headers, assistant, {
    systemPrompt: 'You are Assistant. For every request to roll dice, invoke the Roller agent exactly once and return its result. Never calculate dice yourself. If the user asked for two dice, do not invent a second result and do not stop until Roller returns both.',
    approvalConfig: {
      ...asRecord(assistant.approvalConfig),
      agentToolIds: [fixtures.rollerId],
      approvalRequiredAgentToolIds: [fixtures.rollerId],
    },
  });

  return { skillId: importedSkillId, parentAgentId: fixtures.assistantId, childAgentId: fixtures.rollerId };
}

export function readImportedSkillId(payload: unknown): string {
  const root = asRecord(payload);
  const single = asRecord(root.single);
  const skill = asRecord(single.skill);
  const importedId = skill.id;
  if (root.type !== 'single' || importedId !== skillId) throw new Error('Roll-dice import did not return the exact expected skill id.');
  return importedId;
}

async function saveAgent(
  request: APIRequestContext,
  baseUrl: URL,
  headers: Record<string, string>,
  current: AgentRecord,
  updates: Record<string, unknown>,
): Promise<void> {
  const response = await request.put(new URL(`/api/settings/agents/${encodeURIComponent(current.id)}`, baseUrl).toString(), {
    headers,
    data: { ...current, ...updates },
  });
  await requireSuccess(response, `configure agent ${current.id}`);
  const saved = asRecord(asRecord(await response.json()).agent);
  expect(saved.id, `agent save must return ${current.id}`).toBe(current.id);
}

function requireAgent(agents: readonly AgentRecord[], id: string): AgentRecord {
  const agent = agents.find((candidate) => candidate.id === id);
  if (!agent) throw new Error(`Public fixture agent ${id} was not returned by the settings API.`);
  return agent;
}

function asRecord(value: unknown): Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {};
}

async function requireSuccess(response: APIResponse, operation: string): Promise<void> {
  if (!response.ok()) throw new Error(`${operation} failed with ${response.status()}: ${(await response.text()).slice(0, 500)}`);
}

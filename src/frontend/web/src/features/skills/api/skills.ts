import { apiClient } from '@/lib/apiClient';
import { readApiArray, readApiBoolean, readApiRecord, readApiString, readApiStringArray } from '@/lib/apiResponse';
import {
  bundleImportResponseSchema,
  skillImportResultSchema,
  skillInspectionSchema,
  skillSchema,
  type SkillImportResult,
  type Skill,
  type SkillInspection,
} from '../schemas/skillSchema';

const normalizeStringRecord = (value: unknown): Record<string, string> => {
  const record = readApiRecord({ sourceMetadata: value }, 'sourceMetadata');
  if (!record) {
    return {};
  }

  return Object.fromEntries(
    Object.entries(record).map(([key, entryValue]) => [
      key,
      typeof entryValue === 'string' ? entryValue : String(entryValue ?? ''),
    ])
  );
};

function normalizeSkill(value: unknown): Skill {
  const normalized = {
    id: readApiString(value, 'id') ?? '',
    name: readApiString(value, 'name') ?? '',
    description: readApiString(value, 'description') ?? '',
    packageHash: readApiString(value, 'packageHash') ?? '',
    status: readApiString(value, 'status') ?? 'ready',
    packagePath: readApiString(value, 'packagePath') ?? '',
    hasResources: readApiBoolean(value, 'hasResources') ?? false,
    hasScripts: readApiBoolean(value, 'hasScripts') ?? false,
    enabled: readApiBoolean(value, 'enabled') ?? true,
    envKeys: readApiStringArray(value, 'envKeys') ?? [],
    importTimestamp: readApiString(value, 'importTimestamp') ?? '',
    sourceMetadata: normalizeStringRecord(
      readApiRecord(value, 'sourceMetadata') ?? {}
    ),
    scope: readApiString(value, 'scope') ?? 'system',
    ownerUserId: readApiString(value, 'ownerUserId') ?? null,
  };

  return skillSchema.parse(normalized);
}

function normalizeSkillInspection(value: unknown): SkillInspection {
  const record = readApiRecord({ payload: value }, 'payload') ?? {};
  const normalized = {
    skill: normalizeSkill(record.skill ?? {}),
    instructionsMarkdown:
      readApiString(record, 'instructionsMarkdown') ?? '',
    resourcePaths: readApiArray(record, 'resourcePaths').map(String),
    scriptPaths: readApiArray(record, 'scriptPaths').map(String),
  };

  return skillInspectionSchema.parse(normalized);
}

function normalizeSkillImportResult(value: unknown): SkillImportResult {
  const record = readApiRecord({ payload: value }, 'payload') ?? {};
  const type = readApiString(record, 'type');
  if (type === 'bundle') {
    const bundleRecord = readApiRecord(record, 'bundle') ?? {};
    const skills = readApiArray(bundleRecord, 'skills').map(normalizeSkillInspection);
    return skillImportResultSchema.parse({
      type: 'bundle',
      single: null,
      bundle: bundleImportResponseSchema.parse({ skills }),
    });
  }

  const single = normalizeSkillInspection(record.single ?? {});
  return skillImportResultSchema.parse({
    type: 'single',
    single,
    bundle: null,
  });
}

const buildSkillFormData = (
  file: File,
  options: {
    sourceMetadataJson?: string;
  }
): FormData => {
  const formData = new FormData();
  formData.append('file', file);
  if (options.sourceMetadataJson?.trim())
    formData.append('sourceMetadata', options.sourceMetadataJson.trim());
  return formData;
};

export async function fetchSkills(): Promise<Skill[]> {
  const response = await apiClient.get('/skills');
  return readApiArray(response.data, 'skills').map(normalizeSkill);
}

export async function fetchSkill(skillId: string): Promise<SkillInspection> {
  const response = await apiClient.get(`/skills/${skillId}`);
  return normalizeSkillInspection(response.data);
}

export async function validateSkillImport(
  file: File,
  options: { sourceMetadataJson?: string } = {}
): Promise<SkillImportResult> {
  const response = await apiClient.post('/skills/validate', buildSkillFormData(file, options), {
    headers: { 'Content-Type': 'multipart/form-data' },
  });
  return normalizeSkillImportResult(response.data);
}

export async function importSkill(
  file: File,
  options: { sourceMetadataJson?: string } = {}
): Promise<SkillImportResult> {
  const response = await apiClient.post('/skills/import', buildSkillFormData(file, options), {
    headers: { 'Content-Type': 'multipart/form-data' },
  });
  return normalizeSkillImportResult(response.data);
}

export async function deleteSkill(skillId: string): Promise<void> {
  await apiClient.delete(`/skills/${skillId}`);
}

export async function saveSkill(skillId: string, data: { enabled: boolean; scope: 'system' | 'user' }): Promise<Skill> {
  const response = await apiClient.post(`/skills/${skillId}`, data);
  return normalizeSkill(response.data);
}

export async function listUserEnvironmentKeys(): Promise<string[]> {
  const response = await apiClient.get('/credentials/user');
  return readApiStringArray(response.data, 'keys') ?? [];
}

export async function saveUserEnvironmentValues(entries: Record<string, string>): Promise<void> {
  await apiClient.put('/credentials/user', { entries });
}

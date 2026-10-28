const asRecord = (value: unknown): Record<string, unknown> => {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return {};
  }

  return value as Record<string, unknown>;
};

const findRecordValue = (record: Record<string, unknown>, key: string): unknown => {
  for (const [entryKey, entryValue] of Object.entries(record)) {
    if (entryKey === key) {
      return entryValue;
    }
  }

  return undefined;
};

export const readApiField = <T>(
  value: unknown,
  camelKey: string
): T | undefined => {
  const record = asRecord(value);

  const camelValue = findRecordValue(record, camelKey);
  if (camelValue !== undefined) {
    return camelValue as T;
  }

  return undefined;
};

export const readApiString = (
  value: unknown,
  camelKey: string
): string | undefined => {
  const field = readApiField<unknown>(value, camelKey);
  return typeof field === 'string' ? field : undefined;
};

export const readApiNullableString = (
  value: unknown,
  camelKey: string
): string | null | undefined => {
  const field = readApiField<unknown>(value, camelKey);
  if (field == null) {
    return field;
  }

  return typeof field === 'string' ? field : undefined;
};

export const readApiBoolean = (
  value: unknown,
  camelKey: string
): boolean | undefined => {
  const field = readApiField<unknown>(value, camelKey);
  return typeof field === 'boolean' ? field : undefined;
};

export const readApiNumber = (
  value: unknown,
  camelKey: string
): number | undefined => {
  const field = readApiField<unknown>(value, camelKey);
  return typeof field === 'number' ? field : undefined;
};

export const readApiStringArray = (
  value: unknown,
  camelKey: string
): string[] | undefined => {
  const field = readApiField<unknown>(value, camelKey);
  if (!Array.isArray(field)) {
    return undefined;
  }

  return field.map(String);
};

export const readApiRecord = (
  value: unknown,
  camelKey: string
): Record<string, unknown> | undefined => {
  const field = readApiField<unknown>(value, camelKey);
  if (!field || typeof field !== 'object' || Array.isArray(field)) {
    return undefined;
  }

  return field as Record<string, unknown>;
};

export const readApiArray = (value: unknown, camelKey: string): unknown[] => {
  if (Array.isArray(value)) {
    return value;
  }

  const field = readApiField<unknown>(value, camelKey);
  return Array.isArray(field) ? field : [];
};

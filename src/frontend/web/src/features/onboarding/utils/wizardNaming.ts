const NON_ALPHANUMERIC = /[^a-z0-9]+/g;

function slugify(value: string): string {
  return value
    .toLowerCase()
    .replace(NON_ALPHANUMERIC, '-')
    .replace(/^-+|-+$/g, '');
}

function compactSlug(value: string, fallback: string): string {
  const slug = slugify(value);
  if (!slug) return fallback;

  const parts = slug.split('-').filter(Boolean);
  return parts.slice(0, 2).join('-') || fallback;
}

export function makeUniqueName(base: string, existingIds: readonly string[]): string {
  const taken = new Set(existingIds.map((id) => id.toLowerCase()));
  if (!taken.has(base.toLowerCase())) {
    return base;
  }

  let counter = 2;
  while (taken.has(`${base}-${counter}`.toLowerCase())) {
    counter += 1;
  }

  return `${base}-${counter}`;
}

export function getRecommendedProviderModel(providerType: string): string {
  switch (providerType) {
    case 'gemini':
    case 'google':
      return 'gemini-3.5-flash-lite';
    case 'groq':
      return 'llama-3.1-8b-instant';
    case 'ollama':
      return 'gemma4:e4b';
    case 'openai':
      return 'gpt-4.1-mini';
    case 'openrouter':
      return 'openai/gpt-4.1-mini';
    case 'anthropic':
      return 'claude-sonnet-4-0';
    default:
      return '';
  }
}

function getProviderBaseId(providerType: string): string {
  switch (providerType) {
    case 'gemini':
    case 'google':
      return 'google-main';
    case 'groq':
      return 'groq-fast';
    case 'ollama':
      return 'ollama-local';
    case 'openai':
      return 'openai-main';
    case 'openrouter':
      return 'openrouter-main';
    case 'azure-openai':
      return 'azure-openai';
    case 'anthropic':
      return 'anthropic-main';
    default:
      return `${slugify(providerType) || 'provider'}-main`;
  }
}

type ModelPattern = {
  match(value: string): boolean;
  id: string;
};

function resolveModelBaseId(patterns: readonly ModelPattern[], fallback: string): string {
  return patterns.find((pattern) => pattern.match(fallback))?.id ?? '';
}

function getGeminiModelBaseId(modelName: string): string {
  const patterns: readonly ModelPattern[] = [
    { match: (value) => value.includes('flash-lite'), id: 'gemini-flash-lite' },
    { match: (value) => value.includes('flash'), id: 'gemini-flash' },
    {
      match: (value) => value.includes('3.1-pro') || value.includes('gemini-3.1-pro'),
      id: 'gemini-3.1-pro',
    },
    { match: (value) => value.includes('pro'), id: 'gemini-pro' },
  ];

  return resolveModelBaseId(patterns, modelName) || `gemini-${compactSlug(modelName, 'main')}`;
}

function getGroqModelBaseId(modelName: string): string {
  const patterns: readonly ModelPattern[] = [
    { match: (value) => value.includes('instant'), id: 'groq-instant' },
    { match: (value) => value.includes('gpt-oss-20b'), id: 'groq-gpt-oss' },
    { match: (value) => value.includes('versatile'), id: 'groq-versatile' },
  ];

  return resolveModelBaseId(patterns, modelName) || `groq-${compactSlug(modelName, 'main')}`;
}

function getOllamaModelBaseId(modelName: string): string {
  const patterns: readonly ModelPattern[] = [
    { match: (value) => value.startsWith('gemma4'), id: 'ollama-gemma4' },
    { match: (value) => value.startsWith('llama'), id: 'ollama-llama' },
  ];

  return resolveModelBaseId(patterns, modelName) || `ollama-${compactSlug(modelName, 'chat')}`;
}

function getOpenAiModelBaseId(modelName: string): string {
  const patterns: readonly ModelPattern[] = [
    { match: (value) => value.includes('mini'), id: 'openai-mini' },
    { match: (value) => value.includes('gpt-4o'), id: 'openai-4o' },
  ];

  return resolveModelBaseId(patterns, modelName) || `openai-${compactSlug(modelName, 'chat')}`;
}

export function getGeneratedProviderId(
  providerType: string,
  existingIds: readonly string[]
): string {
  return makeUniqueName(getProviderBaseId(providerType), existingIds);
}

export function getGeneratedModelId(
  providerType: string,
  providerModelName: string,
  existingIds: readonly string[]
): string {
  const normalizedModel = providerModelName.trim().toLowerCase();
  const base = (() => {
    switch (providerType) {
      case 'gemini':
      case 'google':
        return getGeminiModelBaseId(normalizedModel);
      case 'groq':
        return getGroqModelBaseId(normalizedModel);
      case 'ollama':
        return getOllamaModelBaseId(normalizedModel);
      case 'openai':
        return getOpenAiModelBaseId(normalizedModel);
      default:
        return `${slugify(providerType) || 'model'}-${compactSlug(normalizedModel, 'main')}`;
    }
  })();

  return makeUniqueName(base, existingIds);
}

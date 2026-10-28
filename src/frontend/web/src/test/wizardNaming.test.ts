import {
    getGeneratedModelId,
    getGeneratedProviderId,
    getRecommendedProviderModel,
    makeUniqueName,
} from '@/features/onboarding/utils/wizardNaming';
import { describe, expect, it } from 'vitest';

describe('wizardNaming', () => {
  it('creates numbered names when a base id already exists', () => {
    expect(makeUniqueName('google-main', ['google-main', 'google-main-2'])).toBe('google-main-3');
  });

  it('returns readable provider ids for the recommended onboarding providers', () => {
    expect(getGeneratedProviderId('gemini', [])).toBe('google-main');
    expect(getGeneratedProviderId('groq', [])).toBe('groq-fast');
    expect(getGeneratedProviderId('ollama', [])).toBe('ollama-local');
    expect(getGeneratedProviderId('openrouter', [])).toBe('openrouter-main');
  });

  it('returns readable model ids and avoids collisions', () => {
    expect(getGeneratedModelId('gemini', 'gemini-2.5-flash', [])).toBe('gemini-flash');
    expect(getGeneratedModelId('groq', 'llama-3.1-8b-instant', ['groq-instant'])).toBe(
      'groq-instant-2'
    );
    expect(getGeneratedModelId('ollama', 'gemma4:e4b', ['ollama-gemma4', 'ollama-gemma4-2'])).toBe(
      'ollama-gemma4-3'
    );
  });

  it('keeps onboarding recommendations aligned with the beginner defaults', () => {
    expect(getRecommendedProviderModel('gemini')).toBe('gemini-3.5-flash-lite');
    expect(getRecommendedProviderModel('groq')).toBe('llama-3.1-8b-instant');
    expect(getRecommendedProviderModel('ollama')).toBe('gemma4:e4b');
    expect(getRecommendedProviderModel('openrouter')).toBe('openai/gpt-4.1-mini');
  });
});

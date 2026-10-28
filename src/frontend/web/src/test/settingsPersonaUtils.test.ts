import { describe, expect, it } from 'vitest';
import {
  normalizeApprovalConfig,
  normalizePersonaDefinitions,
  normalizePersonaProfile,
} from '@/features/settings';

describe('persona normalization helpers', () => {
  it('falls back when persona definition arrays are empty or invalid', () => {
    const normalized = normalizePersonaDefinitions({
      styles: 'professional',
      tones: [null, '  ', 'friendly'],
      verbosity_levels: [],
    });

    expect(normalized.styles).toEqual(['professional']);
    expect(normalized.tones).toEqual(['friendly']);
    expect(normalized.verbosity_levels).toEqual(['concise']);
  });

  it('uses the current default when temperature is omitted', () => {
    const definitions = normalizePersonaDefinitions({
      styles: ['professional'],
      tones: ['neutral'],
      verbosity_levels: ['concise'],
    });

    const normalized = normalizePersonaProfile(
      {
        style: 'professional',
        tone: 'neutral',
        verbosity: 'concise',
      },
      definitions
    );

    expect(normalized.temperature).toBe(0.7);
  });

  it('normalizes voice fields and agent identity with safe defaults', () => {
    const definitions = normalizePersonaDefinitions({
      styles: ['professional'],
      tones: ['neutral'],
      verbosity_levels: ['concise'],
    });

    const normalized = normalizePersonaProfile(
      {
        category: 'general',
        style: 'professional',
        tone: 'neutral',
        verbosity: 'concise',
      },
      definitions
    );

    expect(normalized.temperature).toBe(0.7);
    expect(normalized).toEqual({
      temperature: 0.7,
      style: 'professional',
      tone: 'neutral',
      verbosity: 'concise',
      icon: '🤖',
      color: '#0F766E',
    });
  });

  it('normalizes approval config from persisted camelCase payloads', () => {
    const normalized = normalizeApprovalConfig({
      requireMcpIds: ['microsoft-learn-mcp'],
      timeoutSeconds: 480,
      requireMcpFunctionFilters: {
        'microsoft-learn-mcp': ['microsoft_docs_search'],
      },
      disabledMcpFunctions: {
        'microsoft-learn-mcp': ['microsoft_code_sample_search'],
      },
    });

    expect(normalized.requireMcpIds).toEqual(['microsoft-learn-mcp']);
    expect(normalized.timeoutSeconds).toBe(480);
    expect(normalized.requireMcpFunctionFilters).toEqual({
      'microsoft-learn-mcp': ['microsoft_docs_search'],
    });
    expect(normalized.disabledMcpFunctions).toEqual({
      'microsoft-learn-mcp': ['microsoft_code_sample_search'],
    });
  });

  it('defaults missing camelCase approval config fields safely', () => {
    const normalized = normalizeApprovalConfig({
      requireMcpIds: ['filesystem-mcp'],
    });

    expect(normalized.requireMcpIds).toEqual(['filesystem-mcp']);
    expect(normalized.timeoutSeconds).toBe(300);
    expect(normalized.requireMcpFunctionFilters).toEqual({});
    expect(normalized.disabledMcpFunctions).toEqual({});
  });
});

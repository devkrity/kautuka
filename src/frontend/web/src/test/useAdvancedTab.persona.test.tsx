import { waitFor } from '@testing-library/dom';
import { act, renderHook } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const { getAdvancedSettingsMock, saveAdvancedSettingsMock } = vi.hoisted(() => ({
  getAdvancedSettingsMock: vi.fn(),
  saveAdvancedSettingsMock: vi.fn(),
}));

vi.mock('@/features/settings', () => ({
  settingsAdmin: {
    getAdvancedSettings: getAdvancedSettingsMock,
    saveAdvancedSettings: saveAdvancedSettingsMock,
  },
}));

import { useAdvancedTab } from '@/features/settings/hooks/useAdvancedTab';

describe('useAdvancedTab persona definitions behavior', () => {
  beforeEach(() => {
    vi.clearAllMocks();

    getAdvancedSettingsMock.mockResolvedValue({
      data: {
        system: {
          logLevel: 'DEBUG',
          observability: {
            includeBinaryContent: false,
            captureMessageContent: false,
          },
          updates: {
            releaseChannel: 'stable',
          },
          runtimeGrounding: {
            locale: 'en-US',
            timeZone: 'UTC',
          },
        },
        kautuka: {
          workspace: {
            baseRoot: '/var/lib/kautuka/workspaces',
          },
          sandbox: {
            image: 'kautuka-base:latest',
            defaultCpus: 2,
            defaultMemory: '4g',
            defaultTimeout: 120,
          },
          documentIngestion: {
            enabled: false,
            internalParserConnectorId: 'markitdown',
            internalParserToolName: 'convert_to_markdown',
            parserTimeoutSeconds: 30,
            parseRequiredExtensions: '.pdf,.docx,.pptx,.xlsx',
            pdfImageFallbackEnabled: false,
            visionRouteAllowed: false,
          },
        },
        personaDefinitions: {
          styles: ['professional'],
          tones: ['neutral'],
          verbosity_levels: ['concise'],
        },
        providerTypeCatalog: [
          {
            type: 'openai',
            friendlyName: 'OpenAI',
            defaultBaseUrl: 'https://api.openai.com/v1',
            defaultHttpTimeoutSeconds: 30,
            requiresApiKey: true,
            requiresBaseUrl: false,
            supportsHttpTimeoutSeconds: true,
            seedVersion: 1,
          },
        ],
        providerPathPolicy: {
          toolNativePromotionThreshold: 0.8,
          toolMinObservations: 5,
          toolNativeDemotionThreshold: 0.4,
          structuredJsonStrictPromotionThreshold: 0.9,
          structuredJsonMinObservations: 5,
          visionEnableThreshold: 0.8,
          observationDecayWindowDays: 30,
          providerPathFallbackModeEnabled: true,
        },
        memory: {
          embeddingsEnabled: false,
          factPersistThreshold: 0.7,
          factInjectThreshold: 0.6,
          maxInjectedFacts: 10,
          injectionTokenBudget: 1024,
        },
      },
    });

    saveAdvancedSettingsMock.mockImplementation(async (body) => ({ data: body }));
  });

  it('loads persona definitions into editable form state', async () => {
    const { result } = renderHook(() => useAdvancedTab());

    await waitFor(() => expect(result.current.loading).toBe(false));

    expect(result.current.releaseChannel).toBe('stable');
    expect(result.current.locale).toBe('en-US');
    expect(result.current.timeZone).toBe('UTC');
    expect(result.current.personaStyles).toBe('professional');
    expect(result.current.personaTones).toBe('neutral');
    expect(result.current.personaVerbosityLevels).toBe('concise');
  });

  it('saves persona definitions through the aggregate advanced settings payload', async () => {
    const { result } = renderHook(() => useAdvancedTab());
    await waitFor(() => expect(result.current.loading).toBe(false));

    act(() => {
      result.current.setPersonaStyles('professional, analytical');
      result.current.setPersonaTones('neutral');
      result.current.setPersonaVerbosityLevels('concise');
    });

    await act(async () => {
      await result.current.save();
    });

    expect(saveAdvancedSettingsMock).toHaveBeenCalledWith(
      expect.objectContaining({
        personaDefinitions: {
          styles: ['professional', 'analytical'],
          tones: ['neutral'],
          verbosity_levels: ['concise'],
        },
      })
    );
  });

  it('tracks dirty tabs and clears them after a successful aggregate save', async () => {
    const { result } = renderHook(() => useAdvancedTab());
    await waitFor(() => expect(result.current.loading).toBe(false));

    act(() => {
      result.current.setReleaseChannel('preview');
      result.current.setLocale('en-GB');
    });

    expect(result.current.hasUnsavedChanges).toBe(true);
    expect(result.current.dirtyTabs.updates).toBe(true);
    expect(result.current.dirtyTabs.runtime).toBe(true);

    await act(async () => {
      await result.current.save();
    });

    expect(saveAdvancedSettingsMock).toHaveBeenCalledWith(
      expect.objectContaining({
        system: expect.objectContaining({
          observability: expect.objectContaining({
            includeBinaryContent: false,
            captureMessageContent: false,
          }),
          updates: {
            releaseChannel: 'preview',
          },
          runtimeGrounding: {
            locale: 'en-GB',
            timeZone: 'UTC',
          },
        }),
      })
    );
    expect(result.current.releaseChannel).toBe('preview');
    expect(result.current.locale).toBe('en-GB');
    expect(result.current.hasUnsavedChanges).toBe(false);
    expect(result.current.dirtyTabs.updates).toBe(false);
    expect(result.current.dirtyTabs.runtime).toBe(false);
  });

  it('saves provider catalog edits through the aggregate advanced settings payload', async () => {
    const { result } = renderHook(() => useAdvancedTab());
    await waitFor(() => expect(result.current.loading).toBe(false));

    act(() => {
      result.current.setProviderTypeCatalog([
        {
          ...result.current.providerTypeCatalog[0],
          defaultBaseUrl: 'https://proxy.example/v1',
          defaultHttpTimeoutSeconds: 45,
        },
      ]);
    });

    expect(result.current.dirtyTabs['provider-catalog']).toBe(true);

    await act(async () => {
      await result.current.save();
    });

    expect(saveAdvancedSettingsMock).toHaveBeenCalledWith(
      expect.objectContaining({
        providerTypeCatalog: [
          expect.objectContaining({
            type: 'openai',
            defaultBaseUrl: 'https://proxy.example/v1',
            defaultHttpTimeoutSeconds: 45,
          }),
        ],
      })
    );
    expect(result.current.dirtyTabs['provider-catalog']).toBe(false);
  });
});

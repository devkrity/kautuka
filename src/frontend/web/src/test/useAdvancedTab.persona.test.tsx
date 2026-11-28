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

import type { AdvancedSettingsEntry } from '@/features/settings/types/settingsTypes';
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
  it('leaves legacy sandbox entries visible and omits them from unrelated saves until edited', async () => {
    const response = await getAdvancedSettingsMock() as { data: AdvancedSettingsEntry };
    const legacy = { HTTP_PROXY: 'http://legacy.invalid', 'BAD-NAME': 'old', BROKEN: 'one\nline' };
    response.data.kautuka.sandbox.containerEnvironment = legacy;
    response.data.kautuka.sandbox.notices = ['Remove HTTP_PROXY from Sandbox environment settings.'];
    getAdvancedSettingsMock.mockResolvedValue(response);
    saveAdvancedSettingsMock.mockImplementation(async (body: AdvancedSettingsEntry) => ({
      data: {
        ...body,
        kautuka: { ...body.kautuka, sandbox: {
          ...body.kautuka.sandbox,
          containerEnvironment: body.kautuka.sandbox.containerEnvironment ?? legacy,
          notices: body.kautuka.sandbox.containerEnvironment ? [] : response.data.kautuka.sandbox.notices,
        } },
      },
    }));
    const { result } = renderHook(() => useAdvancedTab());
    await waitFor(() => expect(result.current.loading).toBe(false));
    act(() => result.current.setLocale('en-GB'));
    await act(async () => { await result.current.save(); });
    expect(saveAdvancedSettingsMock).toHaveBeenCalledTimes(1);
    const submitted = saveAdvancedSettingsMock.mock.calls[0]?.[0] as AdvancedSettingsEntry;
    expect(submitted.kautuka.sandbox).not.toHaveProperty('containerEnvironment');
    expect(result.current.sandboxEnvironment).toContain('BAD-NAME=old');
    expect(result.current.sandboxEnvironment).toContain('BROKEN=one\nline');
    expect(result.current.kautukaSettings?.sandbox.notices).toHaveLength(1);

    act(() => result.current.setSandboxEnvironment('PYTHONPATH=/custom/python'));
    await act(async () => { await result.current.save(); });
    const edited = saveAdvancedSettingsMock.mock.calls[1]?.[0] as AdvancedSettingsEntry;
    expect(edited.kautuka.sandbox.containerEnvironment).toEqual({ PYTHONPATH: '/custom/python' });
    expect(result.current.kautukaSettings?.sandbox.notices).toEqual([]);
  });

});

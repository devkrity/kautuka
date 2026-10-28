import { ConnectorsPage } from '@/pages/ConnectorsPage';
import { SkillsPage } from '@/pages/SkillsPage';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const {
  catalogRecommendationsHookMock,
  installCatalogRecommendationMock,
  getSystemSettingsMock,
} = vi.hoisted(() => ({
  catalogRecommendationsHookMock: vi.fn(),
  installCatalogRecommendationMock: vi.fn(),
  getSystemSettingsMock: vi.fn(),
}));

vi.mock('@/features/auth', () => ({
  useCurrentUser: () => ({
    data: {
      id: 'user-1',
      role: 'admin',
      canImportConnectors: true,
      canImportSkills: true,
    },
  }),
}));

vi.mock('@/features/settings', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/features/settings')>();
  return {
    ...actual,
    settingsAdmin: {
      ...actual.settingsAdmin,
      installCatalogRecommendation: installCatalogRecommendationMock,
      getSystemSettings: getSystemSettingsMock,
    },
    useCatalogRecommendations: catalogRecommendationsHookMock,
    useInstallCatalogRecommendation: () => ({
      isPending: false,
      variables: null,
      mutate: (recommendation: { kind: string; id: string }) =>
        installCatalogRecommendationMock(recommendation.kind, recommendation.id),
    }),
  };
});

vi.mock('@/features/connectors', () => ({
  ConnectorCard: ({ connector }: { connector: { name: string } }) => <div>{connector.name}</div>,
  ConnectorFormModal: () => null,
  ImportModal: () => null,
  blankDraft: () => ({ id: '', name: '' }),
  buildSavePayload: (value: unknown) => value,
  entryToDraft: (value: unknown) => value,
  useCompleteConnectorOAuth: () => ({ isPending: false, mutate: vi.fn() }),
  useConnectors: () => ({ data: [], isLoading: false, error: null }),
  useDeleteConnector: () => ({ isPending: false, mutate: vi.fn() }),
  useSaveConnector: () => ({ isPending: false, mutate: vi.fn() }),
  useSaveConnectorCredentials: () => ({ isPending: false, mutate: vi.fn() }),
  useSaveConnectorDiscovery: () => ({ isPending: false, mutate: vi.fn() }),
}));

vi.mock('@/features/skills', () => ({
  SkillCard: ({ skill }: { skill: { name: string } }) => <div>{skill.name}</div>,
  SkillImportPanel: () => <div>Skill import panel</div>,
  useDeleteSkill: () => ({ isPending: false, mutate: vi.fn() }),
  useImportSkill: () => ({ isPending: false, mutateAsync: vi.fn() }),
  useSaveSkill: () => ({ isPending: false, mutate: vi.fn() }),
  useSaveUserEnvironmentValues: () => ({ isPending: false, mutate: vi.fn() }),
  useSkill: () => ({ data: null }),
  useSkills: () => ({ data: [], isLoading: false, error: null }),
  useUserEnvironmentKeys: () => ({ data: [] }),
  useValidateSkillImport: () => ({ isPending: false, mutateAsync: vi.fn() }),
}));

function renderWithQueryClient(node: React.ReactNode) {
  return render(<QueryClientProvider client={new QueryClient()}>{node}</QueryClientProvider>);
}

describe('Catalog recommendations on management pages', () => {
  beforeEach(() => {
    getSystemSettingsMock.mockReset();
    catalogRecommendationsHookMock.mockReset();
    installCatalogRecommendationMock.mockReset();

    getSystemSettingsMock.mockResolvedValue({
      data: {
        kautuka: {
          documentIngestion: {
            enabled: false,
            internalParserConnectorId: 'markitdown',
            internalParserToolName: 'convert_to_markdown',
          },
        },
      },
    });
    catalogRecommendationsHookMock.mockReturnValue({
      data: {
        connectors: [],
        skills: [],
        message: null,
      },
      isLoading: false,
      isError: false,
    });
    installCatalogRecommendationMock.mockResolvedValue({ data: { saved: true } });
  });

  it('shows recommended connectors on the Connectors page and installs them', async () => {
    catalogRecommendationsHookMock.mockReturnValue({
      data: {
        connectors: [
          {
            id: 'kautuka-web-tools',
            kind: 'connector',
            title: 'Kautuka Web Tools',
            summary: 'Hosted browser automation tools.',
            publisher: 'Kautuka',
            version: '1.0.0',
            isAvailable: false,
            enabled: false,
            canInstall: true,
            trustKind: 'verified_by_kautuka',
            installDisabledReason: null,
          },
        ],
        skills: [],
        message: null,
      },
      isLoading: false,
      isError: false,
    });

    renderWithQueryClient(<ConnectorsPage />);

    expect(await screen.findByText('Kautuka Web Tools')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Install' }));

    await waitFor(() => {
      expect(installCatalogRecommendationMock).toHaveBeenCalledWith(
        'connector',
        'kautuka-web-tools'
      );
    });
  });

  it('shows recommended skills on the Skills page and installs them', async () => {
    catalogRecommendationsHookMock.mockReturnValue({
      data: {
        connectors: [],
        skills: [
          {
            id: 'kautuka-docs',
            kind: 'skill',
            title: 'Kautuka Docs',
            summary: 'Hosted product guidance skill.',
            publisher: 'Kautuka',
            version: '1.0.0',
            isAvailable: false,
            enabled: false,
            canInstall: true,
            trustKind: 'verified_by_kautuka',
            installDisabledReason: null,
          },
        ],
        message: null,
      },
      isLoading: false,
      isError: false,
    });

    renderWithQueryClient(<SkillsPage />);

    expect(await screen.findByText('Kautuka Docs')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Install' }));

    await waitFor(() => {
      expect(installCatalogRecommendationMock).toHaveBeenCalledWith(
        'skill',
        'kautuka-docs'
      );
    });
  });

  it('shows a plain hosted catalog unavailable message', async () => {
    catalogRecommendationsHookMock.mockReturnValue({
      data: {
        connectors: [],
        skills: [],
        message: 'Hosted catalog is currently unreachable. Check again later.',
      },
      isLoading: false,
      isError: false,
    });

    renderWithQueryClient(<ConnectorsPage />);

    expect(
      await screen.findByText('Hosted catalog is currently unreachable. Check again later.')
    ).toBeInTheDocument();
  });
});
import { render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi, afterEach } from 'vitest';
import * as SettingsAdminModule from '@/features/settings/api/settingsAdmin';
import { ProviderForm } from '@/features/settings/components/ProviderForm';

const DEFAULT_FORM = { id: '', type: 'openai', base_url: '', api_key: '', http_timeout_seconds: '30' };
const PROVIDER_TYPES = [
  {
    type: 'ollama',
    friendlyName: 'Ollama',
    requiresApiKey: false,
    requiresBaseUrl: true,
    supportsHttpTimeoutSeconds: true,
  },
  {
    type: 'openai',
    friendlyName: 'OpenAI',
    requiresApiKey: true,
    requiresBaseUrl: false,
    supportsHttpTimeoutSeconds: true,
  },
  {
    type: 'azure-openai',
    friendlyName: 'Azure OpenAI',
    requiresApiKey: true,
    requiresBaseUrl: true,
    supportsHttpTimeoutSeconds: true,
  },
  {
    type: 'groq',
    friendlyName: 'Groq',
    requiresApiKey: true,
    requiresBaseUrl: false,
    supportsHttpTimeoutSeconds: true,
  },
];

describe('ProviderForm — dynamic provider types', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('fetches provider types on mount and populates the type dropdown', async () => {
    const listProviderTypesSpy = vi.spyOn(SettingsAdminModule.settingsAdmin, 'listProviderTypes');
    listProviderTypesSpy.mockResolvedValue({ data: { providerTypes: PROVIDER_TYPES } } as never);

    render(
      <ProviderForm
        editingId=""
        form={DEFAULT_FORM}
        saving={false}
        onChange={vi.fn()}
        onCancel={vi.fn()}
        onSave={vi.fn()}
      />
    );

    await waitFor(() => expect(listProviderTypesSpy).toHaveBeenCalledOnce());
    await waitFor(() => expect(screen.getByRole('option', { name: 'Groq' })).toBeInTheDocument());

    const options = screen.getAllByRole('option');
    const labels = options.map((o) => o.textContent);
    expect(labels).toContain('Ollama');
    expect(labels).toContain('OpenAI');
    expect(labels).toContain('Azure OpenAI');
    expect(labels).toContain('Groq');
  });

  it('shows Base URL field for every provider type', async () => {
    const listProviderTypesSpy = vi.spyOn(SettingsAdminModule.settingsAdmin, 'listProviderTypes');
    listProviderTypesSpy.mockResolvedValue({ data: { providerTypes: PROVIDER_TYPES } } as never);

    const { rerender } = render(
      <ProviderForm
        editingId=""
        form={{ ...DEFAULT_FORM, type: 'azure-openai' }}
        saving={false}
        onChange={vi.fn()}
        onCancel={vi.fn()}
        onSave={vi.fn()}
      />
    );
    await waitFor(() => expect(listProviderTypesSpy).toHaveBeenCalledOnce());
    await waitFor(() => screen.getByRole('option', { name: 'Azure OpenAI' }));

    expect(screen.getByText('Base URL')).toBeInTheDocument();
    expect(
      screen.getByText(/copy the current catalog default when the provider is created/i)
    ).toBeInTheDocument();

    rerender(
      <ProviderForm
        editingId=""
        form={{ ...DEFAULT_FORM, type: 'groq' }}
        saving={false}
        onChange={vi.fn()}
        onCancel={vi.fn()}
        onSave={vi.fn()}
      />
    );
    expect(screen.getByText('Base URL')).toBeInTheDocument();
  });

  it('shows API Key field for types that require it, not for ollama', async () => {
    const listProviderTypesSpy = vi.spyOn(SettingsAdminModule.settingsAdmin, 'listProviderTypes');
    listProviderTypesSpy.mockResolvedValue({ data: { providerTypes: PROVIDER_TYPES } } as never);

    const { rerender } = render(
      <ProviderForm
        editingId=""
        form={{ ...DEFAULT_FORM, type: 'openai' }}
        saving={false}
        onChange={vi.fn()}
        onCancel={vi.fn()}
        onSave={vi.fn()}
      />
    );
    await waitFor(() => screen.getByRole('option', { name: 'OpenAI' }));

    expect(screen.getByText('API Key')).toBeInTheDocument();

    rerender(
      <ProviderForm
        editingId=""
        form={{ ...DEFAULT_FORM, type: 'ollama' }}
        saving={false}
        onChange={vi.fn()}
        onCancel={vi.fn()}
        onSave={vi.fn()}
      />
    );
    expect(screen.queryByText('API Key')).not.toBeInTheDocument();
  });

  it('shows HTTP timeout field when the provider type supports it', async () => {
    const listProviderTypesSpy = vi.spyOn(SettingsAdminModule.settingsAdmin, 'listProviderTypes');
    listProviderTypesSpy.mockResolvedValue({ data: { providerTypes: PROVIDER_TYPES } } as never);

    render(
      <ProviderForm
        editingId=""
        form={{ ...DEFAULT_FORM, type: 'ollama' }}
        saving={false}
        onChange={vi.fn()}
        onCancel={vi.fn()}
        onSave={vi.fn()}
      />
    );

    await waitFor(() => screen.getByRole('option', { name: 'Ollama' }));

    expect(screen.getByText('HTTP Timeout (seconds)')).toBeInTheDocument();
    expect(screen.getByDisplayValue('30')).toBeInTheDocument();
    expect(
      screen.getByText(/This timeout applies when Kautuka tests the provider/i)
    ).toBeInTheDocument();
  });

  it('renders empty dropdown while provider types are loading', () => {
    const listProviderTypesSpy = vi.spyOn(SettingsAdminModule.settingsAdmin, 'listProviderTypes');
    listProviderTypesSpy.mockReturnValue(new Promise(() => {})); // never resolves
    render(
      <ProviderForm
        editingId=""
        form={DEFAULT_FORM}
        saving={false}
        onChange={vi.fn()}
        onCancel={vi.fn()}
        onSave={vi.fn()}
      />
    );
    expect(screen.queryAllByRole('option')).toHaveLength(0);
  });

  it('shows the internal provider id field in wizard mode with guidance', async () => {
    const listProviderTypesSpy = vi.spyOn(SettingsAdminModule.settingsAdmin, 'listProviderTypes');
    listProviderTypesSpy.mockResolvedValue({ data: { providerTypes: PROVIDER_TYPES } } as never);

    render(
      <ProviderForm
        variant="wizard"
        editingId=""
        form={DEFAULT_FORM}
        generatedProviderId="google-main"
        saving={false}
        onChange={vi.fn()}
        onCancel={vi.fn()}
        onSave={vi.fn()}
      />
    );

    await waitFor(() => screen.getByRole('option', { name: 'OpenAI' }));

    expect(screen.getByText('Kautuka name')).toBeInTheDocument();
    expect(screen.getByText(/Kautuka will save this as/i)).toBeInTheDocument();
    expect(screen.getByText('google-main')).toBeInTheDocument();
    expect(screen.getByText('You do not need to make up a system name here.')).toBeInTheDocument();
    expect(screen.getByText("Choose where your helper's brain lives.")).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Test and Continue' })).toBeInTheDocument();
    expect(screen.queryByText('Provider Name')).not.toBeInTheDocument();
  });

  it('shows wizard validation feedback when provided', async () => {
    const listProviderTypesSpy = vi.spyOn(SettingsAdminModule.settingsAdmin, 'listProviderTypes');
    listProviderTypesSpy.mockResolvedValue({ data: { providerTypes: PROVIDER_TYPES } } as never);

    render(
      <ProviderForm
        variant="wizard"
        editingId=""
        form={{ ...DEFAULT_FORM, type: 'groq' }}
        generatedProviderId="groq-fast"
        result={{ success: false, message: 'Provider test failed.' }}
        saving={false}
        onChange={vi.fn()}
        onCancel={vi.fn()}
        onSave={vi.fn()}
      />
    );

    await waitFor(() => screen.getByRole('option', { name: 'Groq' }));

    expect(screen.getByText('Provider test failed.')).toBeInTheDocument();
  });
});

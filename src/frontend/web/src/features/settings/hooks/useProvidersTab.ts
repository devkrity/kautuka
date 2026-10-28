import { useEffect, useMemo, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { settingsAdmin } from '@/features/settings';
import { apiError } from '../utils/settingsUtils';
import type { ProviderEntry, ProviderTypeInfo } from '../types/settingsTypes';

const PROVIDER_KIND_TEST = ['te', 'st'].join('');
const DEFAULT_HTTP_TIMEOUT_SECONDS = '30';

export interface ProviderFormState {
  id: string;
  type: string;
  base_url: string;
  api_key: string;
  http_timeout_seconds: string;
}

const DEFAULT_FORM: ProviderFormState = {
  id: '',
  type: 'ollama',
  base_url: '',
  api_key: '',
  http_timeout_seconds: DEFAULT_HTTP_TIMEOUT_SECONDS,
};

function normalizeHttpTimeoutSeconds(value: string): number | null | 'invalid' {
  const trimmed = value.trim() || DEFAULT_HTTP_TIMEOUT_SECONDS;

  const parsed = Number(trimmed);
  if (!Number.isFinite(parsed) || parsed <= 0) {
    return 'invalid';
  }

  return parsed;
}

export function providerChip(type: string): { label: string; className: string } {
  if (type === 'ollama' || type === PROVIDER_KIND_TEST) {
    return {
      label: 'Local',
      className:
        'badge border-attention/30 bg-attention/10 text-attention',
    };
  }
  return {
    label: 'Active',
    className:
      'badge border-done/30 bg-done/10 text-done',
  };
}

export function useProvidersTab() {
  const queryClient = useQueryClient();
  const [providers, setProviders] = useState<ProviderEntry[]>([]);
  const [providerTypes, setProviderTypes] = useState<ProviderTypeInfo[]>([]);
  const [loading, setLoading] = useState(true);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [form, setForm] = useState<ProviderFormState>(DEFAULT_FORM);
  const [result, setResult] = useState<{ success: boolean; message: string } | null>(null);
  const [saving, setSaving] = useState(false);

  const load = async () => {
    setLoading(true);
    try {
      const [providersRes, typesRes] = await Promise.all([
        settingsAdmin.listProviders(),
        settingsAdmin.listProviderTypes(),
      ]);
      setProviders(providersRes.data.providers);
      setProviderTypes(typesRes.data.providerTypes);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void load();
  }, []);

  const startCreate = () => {
    setEditingId('');
    setForm(DEFAULT_FORM);
    setResult(null);
  };

  const startEdit = (provider: ProviderEntry) => {
    setEditingId(provider.id);
    setForm({
      id: provider.id,
      type: provider.type,
      base_url: String(provider.settings?.base_url ?? ''),
      api_key: '',
      http_timeout_seconds: String(provider.settings?.http_timeout_seconds ?? DEFAULT_HTTP_TIMEOUT_SECONDS),
    });
    setResult(null);
  };

  const cancelEdit = () => {
    setEditingId(null);
    setForm(DEFAULT_FORM);
  };

  const save = async () => {
    if (!form.id.trim()) {
      setResult({ success: false, message: 'Provider id is required.' });
      return;
    }
    if (!form.type.trim()) {
      setResult({ success: false, message: 'Provider type is required.' });
      return;
    }

    const httpTimeoutSeconds = normalizeHttpTimeoutSeconds(form.http_timeout_seconds);
    if (httpTimeoutSeconds === 'invalid') {
      setResult({ success: false, message: 'HTTP timeout must be a positive number of seconds.' });
      return;
    }

    setSaving(true);
    setResult(null);
    try {
      await settingsAdmin.saveProvider({
        id: form.id.trim(),
        type: form.type,
        settings: {
          base_url: form.base_url.trim() || null,
          api_key: form.api_key.trim() || null,
          http_timeout_seconds: httpTimeoutSeconds,
        },
      });
      setResult({ success: true, message: 'Provider saved.' });
      cancelEdit();
      await load();
      await queryClient.invalidateQueries({ queryKey: ['system-setup-status'] });
    } catch (err) {
      setResult({ success: false, message: apiError(err) });
    } finally {
      setSaving(false);
    }
  };

  const remove = async (id: string) => {
    try {
      await settingsAdmin.deleteProvider(id);
      setResult({ success: true, message: `Provider ${id} deleted.` });
      await load();
      await queryClient.invalidateQueries({ queryKey: ['system-setup-status'] });
    } catch (err) {
      setResult({ success: false, message: apiError(err) });
    }
  };

  const providersWithKeys = providers.filter((p) => p.hasApiKey).length;
  const providerStatus = loading
    ? 'Checking provider inventory...'
    : `${providers.length} provider profile(s) configured`;
  const providerRecent =
    result?.message ??
    (providers[0] ? `Latest profile: ${providers[0].id}` : 'No provider changes yet');
  const providerSecurity =
    providersWithKeys > 0
      ? `${providersWithKeys} provider key(s) configured`
      : 'No provider keys configured';

  const statusItems = useMemo(
    () => [
      { title: 'Global Status', value: providerStatus },
      { title: 'Recent Activity', value: providerRecent },
      { title: 'Security', value: providerSecurity },
    ],
    [providerRecent, providerSecurity, providerStatus]
  );

  return {
    providers,
    providerTypes,
    loading,
    editingId,
    form,
    result,
    saving,
    statusItems,
    setForm,
    startCreate,
    startEdit,
    cancelEdit,
    save,
    remove,
  };
}

import { settingsAdmin } from '@/features/settings';
import { useEffect, useMemo, useState } from 'react';
import type { DiscoveredModelEntry, ModelEntry, ProviderEntry } from '../types/settingsTypes';
import { apiError } from '../utils/settingsUtils';

const PROVIDER_KIND_TEST = ['te', 'st'].join('');

const readCustomOutputText = (value: unknown): string => {
  if (typeof value === 'string') {
    return value;
  }
  if (typeof value === 'number' || typeof value === 'boolean') {
    return `${value}`;
  }
  return '';
};

const readNumericSetting = (value: unknown): string => {
  if (typeof value === 'number' && Number.isFinite(value)) {
    return `${value}`;
  }

  if (typeof value === 'string') {
    const trimmed = value.trim();
    return trimmed;
  }

  return '';
};

const parsePositiveInteger = (value: string): number | null => {
  const trimmed = value.trim();
  if (trimmed.length === 0) {
    return null;
  }

  const parsed = Number.parseInt(trimmed, 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
};

const parsePercentage = (value: string): number | null => {
  const trimmed = value.trim();
  if (trimmed.length === 0) {
    return null;
  }

  const parsed = Number.parseFloat(trimmed);
  return Number.isFinite(parsed) && parsed > 0 && parsed <= 100 ? parsed : null;
};

export interface ModelFormState {
  id: string;
  providerId: string;
  modelName: string;
  custom_output_text: string;
  contextWindowTokens: string;
  autoCompactThresholdPercent: string;
}

const EMPTY_FORM: ModelFormState = {
  id: '',
  providerId: '',
  modelName: '',
  custom_output_text: '',
  contextWindowTokens: '',
  autoCompactThresholdPercent: '',
};

export function useModelsTab() {
  const [models, setModels] = useState<ModelEntry[]>([]);
  const [providers, setProviders] = useState<ProviderEntry[]>([]);
  const [availableModelNames, setAvailableModelNames] = useState<DiscoveredModelEntry[]>([]);
  const [discoveringModels, setDiscoveringModels] = useState(false);
  const [loading, setLoading] = useState(true);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [form, setForm] = useState<ModelFormState>(EMPTY_FORM);
  const [formCapabilities, setFormCapabilities] = useState<ModelEntry['capabilities']>(null);
  const [result, setResult] = useState<{ success: boolean; message: string } | null>(null);
  const [saving, setSaving] = useState(false);
  const [probingId, setProbingId] = useState<string | null>(null);
  const [confirmedWarningKey, setConfirmedWarningKey] = useState<string | null>(null);

  const buildCandidateKey = (providerId: string, modelName: string) =>
    `${providerId.trim()}::${modelName.trim()}`;

  const load = async () => {
    setLoading(true);
    try {
      const [modelResponse, providerResponse] = await Promise.all([
        settingsAdmin.listModels(),
        settingsAdmin.listProviders(),
      ]);
      setModels(modelResponse.data.models);
      setProviders(providerResponse.data.providers);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void load();
  }, []);

  const discoverModelNames = async (providerId: string, currentModelName?: string) => {
    if (!providerId) {
      setAvailableModelNames([]);
      return;
    }

    setDiscoveringModels(true);
    try {
      const response = await settingsAdmin.discoverModels({ providerId });
      const discovered: DiscoveredModelEntry[] = response.data.models
        .filter(
          (item): item is DiscoveredModelEntry =>
            typeof item?.id === 'string' && typeof item?.displayName === 'string'
        )
        .map((item) => ({
          id: item.id.trim(),
          displayName: item.displayName.trim(),
          description: item.description?.trim() || null,
          supportsChat: typeof item.supportsChat === 'boolean' ? item.supportsChat : null,
          supportsEmbeddings:
            typeof item.supportsEmbeddings === 'boolean' ? item.supportsEmbeddings : null,
          capabilityTags: Array.isArray(item.capabilityTags)
            ? item.capabilityTags.filter((tag): tag is string => typeof tag === 'string')
            : null,
        }))
        .filter((item) => item.id.length > 0);
      const uniqueNames: DiscoveredModelEntry[] = Array.from(
        new Map(discovered.map((item) => [item.id, item])).values()
      );
      const trimmedCurrentName = currentModelName?.trim();
      if (trimmedCurrentName && !uniqueNames.some((item) => item.id === trimmedCurrentName)) {
        setAvailableModelNames([
          {
            id: trimmedCurrentName,
            displayName: trimmedCurrentName,
            description: null,
            supportsChat: null,
            supportsEmbeddings: null,
            capabilityTags: null,
          },
          ...uniqueNames,
        ]);
      } else {
        setAvailableModelNames(uniqueNames);
      }
    } catch {
      setAvailableModelNames(
        currentModelName?.trim()
          ? [
              {
                id: currentModelName.trim(),
                displayName: currentModelName.trim(),
                description: null,
                supportsChat: null,
                supportsEmbeddings: null,
                capabilityTags: null,
              },
            ]
          : []
      );
    } finally {
      setDiscoveringModels(false);
    }
  };

  useEffect(() => {
    if (editingId === null || !form.providerId) {
      return;
    }
    void discoverModelNames(form.providerId, form.modelName);
  }, [editingId, form.providerId, form.modelName]);

  useEffect(() => {
    setConfirmedWarningKey(null);
  }, [form.providerId, form.modelName]);

  const startCreate = () => {
    setEditingId('');
    setAvailableModelNames([]);
    setForm({ ...EMPTY_FORM, providerId: providers[0]?.id ?? '' });
    setFormCapabilities(null);
    setConfirmedWarningKey(null);
    setResult(null);
  };

  const startEdit = (model: ModelEntry) => {
    setEditingId(model.id);
    setAvailableModelNames([]);
    setForm({
      id: model.id,
      providerId: model.providerId,
      modelName: model.modelName,
      custom_output_text: readCustomOutputText(model.settings?.custom_output_text),
      contextWindowTokens: readNumericSetting(model.settings?.contextWindowTokens),
      autoCompactThresholdPercent: readNumericSetting(model.settings?.autoCompactThresholdPercent),
    });
    setFormCapabilities(model.capabilities ?? null);
    setConfirmedWarningKey(null);
    setResult(null);
  };

  const cancelEdit = () => {
    setEditingId(null);
    setAvailableModelNames([]);
    setForm(EMPTY_FORM);
    setFormCapabilities(null);
    setConfirmedWarningKey(null);
  };

  const save = async () => {
    if (!form.id.trim() || !form.providerId.trim() || !form.modelName.trim()) {
      setResult({ success: false, message: 'Model id, provider, and model name are required.' });
      return;
    }

    setSaving(true);
    setResult(null);
    try {
      const settings: Record<string, unknown> = {};
      const providerType = providers.find((provider) => provider.id === form.providerId)?.type;
      if (providerType === PROVIDER_KIND_TEST && form.custom_output_text.trim()) {
        settings.custom_output_text = form.custom_output_text.trim();
      }

      const contextWindowTokens = parsePositiveInteger(form.contextWindowTokens);
      if (contextWindowTokens !== null) {
        settings.contextWindowTokens = contextWindowTokens;
      }

      const autoCompactThresholdPercent = parsePercentage(form.autoCompactThresholdPercent);
      if (autoCompactThresholdPercent !== null) {
        settings.autoCompactThresholdPercent = autoCompactThresholdPercent;
      }

      const candidateKey = buildCandidateKey(form.providerId, form.modelName);
      const probeResponse = await settingsAdmin.probeModelCandidate({
        providerId: form.providerId,
        modelName: form.modelName.trim(),
      });
      const probedCapabilities = probeResponse.data.capabilities ?? null;
      const warningMessage = probeResponse.data.warningMessage?.trim() ?? '';
      setFormCapabilities(probedCapabilities);

      if (warningMessage && confirmedWarningKey !== candidateKey) {
        setConfirmedWarningKey(candidateKey);
        setResult({
          success: false,
          message: `${warningMessage}. Save again to continue, or choose a different model.`,
        });
        return;
      }

      await settingsAdmin.saveModel({
        id: form.id.trim(),
        providerId: form.providerId,
        modelName: form.modelName.trim(),
        settings,
        capabilities: probedCapabilities,
      });

      setResult({
        success: true,
        message: warningMessage ? `Model saved. ${warningMessage}.` : 'Model saved.',
      });
      cancelEdit();
      await load();
    } catch (err) {
      setResult({ success: false, message: apiError(err) });
    } finally {
      setSaving(false);
    }
  };

  const remove = async (id: string) => {
    try {
      await settingsAdmin.deleteModel(id);
      setResult({ success: true, message: `Model ${id} deleted.` });
      await load();
    } catch (err) {
      setResult({ success: false, message: apiError(err) });
    }
  };

  const probeModel = async (modelId: string) => {
    setProbingId(modelId);
    setResult(null);
    try {
      const response = await settingsAdmin.probeModel(modelId);
      setFormCapabilities(response.data.capabilities ?? null);
      setConfirmedWarningKey(null);
      setResult({ success: true, message: `Capability check complete for ${modelId}.` });
      await load();
    } catch (err) {
      setResult({ success: false, message: apiError(err) });
    } finally {
      setProbingId(null);
    }
  };

  const selectedProviderType = providers.find((provider) => provider.id === form.providerId)?.type;
  const providerLabel = (providerId: string): string => {
    const provider = providers.find((item) => item.id === providerId);
    return provider ? `${provider.id} (${provider.type})` : providerId;
  };

  const testModels = models.filter(
    (model) =>
      providers.find((provider) => provider.id === model.providerId)?.type === PROVIDER_KIND_TEST
  ).length;
  const modelsStatus = loading
    ? 'Checking model registry...'
    : `${models.length} model definition(s) available`;
  const modelsRecent =
    result?.message ?? (models[0] ? `Latest model: ${models[0].id}` : 'No model changes yet');
  const modelsSecurity =
    testModels > 0
      ? `${testModels} test model(s) isolated for local validation`
      : 'All models mapped to external/local providers';

  const statusItems = useMemo(
    () => [
      { title: 'Global Status', value: modelsStatus },
      { title: 'Recent Activity', value: modelsRecent },
      { title: 'Security', value: modelsSecurity },
    ],
    [modelsRecent, modelsSecurity, modelsStatus]
  );

  return {
    models,
    providers,
    availableModelNames,
    discoveringModels,
    loading,
    editingId,
    form,
    formCapabilities,
    result,
    saving,
    probingId,
    selectedProviderType,
    statusItems,
    setForm,
    setFormCapabilities,
    startCreate,
    startEdit,
    cancelEdit,
    save,
    remove,
    probeModel,
    discoverModelNames,
    providerLabel,
  };
}

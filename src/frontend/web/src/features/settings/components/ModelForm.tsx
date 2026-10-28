import { Alert } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select } from '@/components/ui/select';
import { LoaderCircle } from 'lucide-react';
import React from 'react';
import type { ModelFormState } from '../hooks/useModelsTab';
import type { DiscoveredModelEntry, ModelEntry, ProviderEntry } from '../types/settingsTypes';
import { CapabilityAdvisory } from './CapabilityAdvisory';
import { ModelDiscoverySelect } from './ModelDiscoverySelect';

const PROVIDER_KIND_TEST = ['te', 'st'].join('');

function getInternalModelPlaceholder(
  providerType?: string,
  variant?: 'wizard' | 'settings'
): string {
  if (variant !== 'wizard') {
    return 'e.g., gpt4-main';
  }

  switch (providerType) {
    case 'openai':
      return 'Choose a short model name, like openai-chat';
    case 'azure-openai':
      return 'Choose a short model name, like azure-chat';
    case 'gemini':
    case 'google':
      return 'Choose a short model name, like gemini-fast';
    case 'anthropic':
      return 'Choose a short model name, like claude-main';
    case 'groq':
      return 'Choose a short model name, like groq-fast';
    case 'ollama':
      return 'Choose a short model name, like ollama-local-chat';
    default:
      return 'Choose a short model name, like assistant-main';
  }
}

function getInternalModelHelpText(providerType?: string, variant?: 'wizard' | 'settings'): string {
  if (variant !== 'wizard') {
    return "Pick something short and memorable — you can't change this later.";
  }

  switch (providerType) {
    case 'openai':
      return "This is Kautuka's internal name for your OpenAI model. Something like openai-chat is enough.";
    case 'azure-openai':
      return "This is Kautuka's internal name for your Azure OpenAI model. Something like azure-chat is enough.";
    case 'gemini':
    case 'google':
      return "This is Kautuka's internal name for your Gemini model. Something like gemini-fast is enough.";
    case 'anthropic':
      return "This is Kautuka's internal name for your Anthropic model. Something like claude-main is enough.";
    case 'groq':
      return "This is Kautuka's internal name for your Groq model. Something like groq-fast is enough.";
    case 'ollama':
      return "This is Kautuka's internal name for your local Ollama model. Something like ollama-local-chat is enough.";
    default:
      return "This is Kautuka's internal name for the model. You will still pick the real provider model below.";
  }
}

export const ModelForm: React.FC<{
  variant?: 'wizard' | 'settings';
  editingId: string;
  form: ModelFormState;
  generatedModelId?: string;
  providers: ProviderEntry[];
  availableModelNames: DiscoveredModelEntry[];
  discoveringModels: boolean;
  formCapabilities: ModelEntry['capabilities'];
  selectedProviderType?: string;
  saving: boolean;
  probingId: string | null;
  cancelLabel?: string;
  onChange(next: ModelFormState): void;
  onSetCapabilities(next: ModelEntry['capabilities']): void;
  onCancel(): void;
  onSave(): void;
  onProbe(id: string): void;
  onDiscover(providerId: string, currentModelName?: string): void;
}> = ({
  variant = 'settings',
  editingId,
  form,
  providers,
  availableModelNames,
  discoveringModels,
  formCapabilities,
  selectedProviderType,
  saving,
  probingId,
  cancelLabel = 'Cancel',
  onChange,
  onSetCapabilities,
  onCancel,
  onSave,
  onProbe,
  onDiscover,
  generatedModelId = '',
}) => {
  let cardTitle = 'Create Model';
  if (editingId) {
    cardTitle = `Edit Model: ${editingId}`;
  } else if (variant === 'wizard') {
    cardTitle = 'Choose Model';
  }
  const selectedProvider =
    providers.find((provider) => provider.id === form.providerId) ?? providers[0];
  const resolvedProviderType = selectedProviderType ?? selectedProvider?.type;

  return (
    <Card>
      <CardHeader>
        <CardTitle>{cardTitle}</CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        {variant === 'wizard' ? (
          <div className="rounded-lg border border-border bg-muted/30 p-3 text-sm">
            <div className="font-medium text-foreground">Kautuka name</div>
            <div className="mt-1 text-muted-foreground">
              Kautuka will save this as{' '}
              <span className="font-medium text-foreground">{generatedModelId}</span>.
            </div>
            <p className="mt-1 text-xs text-muted-foreground">
              You only need to choose the real model. We will name it cleanly for you.
            </p>
          </div>
        ) : (
          <div>
            <Label
              className="mb-1 block"
              title="A short nickname you choose for this model — you'll see this name when picking a model for an agent"
            >
              Model Name
            </Label>
            <Input
              value={form.id}
              disabled={!!editingId}
              onChange={(e) => onChange({ ...form, id: e.target.value })}
              placeholder={getInternalModelPlaceholder(resolvedProviderType, variant)}
              title="Cannot be changed after creation"
            />
            {!editingId && (
              <p className="mt-1 text-xs text-muted-foreground">
                {getInternalModelHelpText(resolvedProviderType, variant)}
              </p>
            )}
          </div>
        )}

        {variant === 'wizard' ? (
          <div className="rounded-lg border border-border bg-muted/30 p-3 text-sm">
            <div className="font-medium text-foreground">Using selected provider</div>
            <div className="mt-1 text-muted-foreground">
              {selectedProvider?.type
                ? `We'll use your ${selectedProvider.type} provider from the previous step.`
                : 'We will use the provider you picked in the previous step.'}
            </div>
          </div>
        ) : (
          <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
            <div>
              <Label className="mb-1 block" title="Select which provider hosts this model">
                Provider
              </Label>
              <Select
                title="The service where this model runs"
                value={form.providerId}
                onChange={(e) => {
                  onSetCapabilities(null);
                  onChange({ ...form, providerId: e.target.value });
                }}
              >
                <option value="">Select provider...</option>
                {[...providers]
                  .sort((a, b) => a.id.localeCompare(b.id))
                  .map((provider) => (
                    <option key={provider.id} value={provider.id}>
                      {provider.id} ({provider.type})
                    </option>
                  ))}
              </Select>
            </div>
          </div>
        )}

        <ModelDiscoverySelect
          providerId={form.providerId}
          providerType={resolvedProviderType}
          modelName={form.modelName}
          availableModelNames={availableModelNames}
          discoveringModels={discoveringModels}
          variant={variant}
          onModelNameChange={(value) => {
            onSetCapabilities(null);
            onChange({ ...form, modelName: value });
          }}
          onRefresh={() => onDiscover(form.providerId, form.modelName)}
        />

        <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
          <div>
            <Label
              className="mb-1 block"
              title="Optional per-model context window override used for estimated usage and auto-compaction thresholds"
            >
              Context Window Tokens
            </Label>
            <Input
              type="number"
              min={1}
              step={1}
              value={form.contextWindowTokens}
              onChange={(e) => onChange({ ...form, contextWindowTokens: e.target.value })}
              placeholder="Defaults to 32000 local / 256000 cloud"
              title="Leave empty to use the provider-class default"
            />
            <p className="mt-1 text-xs text-muted-foreground">
              Leave empty to use the default for this provider class.
            </p>
          </div>

          <div>
            <Label
              className="mb-1 block"
              title="Optional percentage of the context window that triggers auto-compaction before a run"
            >
              Auto-Compact Threshold %
            </Label>
            <Input
              type="number"
              min={1}
              max={100}
              step="0.1"
              value={form.autoCompactThresholdPercent}
              onChange={(e) => onChange({ ...form, autoCompactThresholdPercent: e.target.value })}
              placeholder="e.g., 80"
              title="Leave empty to disable auto-compaction for this model"
            />
            <p className="mt-1 text-xs text-muted-foreground">
              Leave empty to disable automatic compaction for this model.
            </p>
          </div>
        </div>

        {formCapabilities?.agentConversations === false && (
          <Alert variant="attention" className="rounded px-3 py-2">
            This model may not work for agent conversations
          </Alert>
        )}

        {editingId && formCapabilities && variant === 'settings' && (
          <div className="settings-subpanel-compact text-sm">
            <div className="mb-1 font-medium">Capability advisory</div>
            <CapabilityAdvisory capabilities={formCapabilities} />
          </div>
        )}

        {selectedProviderType === PROVIDER_KIND_TEST && variant === 'settings' && (
          <div>
            <Label
              className="mb-1 block"
              title="Hardcoded response for testing without real AI calls"
            >
              Custom Output Text
            </Label>
            <Input
              value={form.custom_output_text}
              onChange={(e) => onChange({ ...form, custom_output_text: e.target.value })}
              placeholder="e.g., This is a test response"
              title="Only for test providers"
            />
          </div>
        )}

        <div className="flex justify-end gap-2">
          <Button variant="secondary" onClick={onCancel}>
            {cancelLabel}
          </Button>
          {editingId && (
            <Button
              variant="secondary"
              onClick={() => onProbe(editingId)}
              disabled={probingId === editingId}
              title="Run a quick capability check on this model"
            >
              {probingId === editingId ? 'Checking...' : 'Check Capabilities'}
            </Button>
          )}
          <Button onClick={onSave} disabled={saving}>
            {saving ? (
              <span className="inline-flex items-center gap-2">
                <LoaderCircle className="h-4 w-4 animate-spin" />
                Checking...
              </span>
            ) : (
              'Save Model'
            )}
          </Button>
        </div>
      </CardContent>
    </Card>
  );
};

import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select } from '@/components/ui/select';
import React from 'react';
import type { DiscoveredModelEntry } from '../types/settingsTypes';

function formatCapabilityTags(model: DiscoveredModelEntry): string {
  return model.capabilityTags?.length ? ` [${model.capabilityTags.join(', ')}]` : '';
}

function getSelectedModelDetails(
  availableModelNames: DiscoveredModelEntry[],
  modelName: string
): string | null {
  const selectedModel = availableModelNames.find((item) => item.id === modelName);
  if (!selectedModel) {
    return null;
  }

  const detailParts = [selectedModel.description, selectedModel.capabilityTags?.join(', ')].filter(
    (value): value is string => typeof value === 'string' && value.trim().length > 0
  );

  return detailParts.length > 0 ? detailParts.join(' • ') : null;
}

function getProviderModelPlaceholder(
  providerType?: string,
  variant?: 'wizard' | 'settings'
): string {
  if (variant !== 'wizard') {
    return 'e.g., gemma2:7b for Ollama';
  }

  switch (providerType) {
    case 'openai':
      return 'Choose the exact model from that provider, like gpt-4o or gpt-4.1-mini';
    case 'azure-openai':
      return 'Choose the deployed Azure model name, like gpt-4o';
    case 'gemini':
    case 'google':
      return 'Choose the exact model from that provider, like gemini-3.5-flash-lite';
    case 'anthropic':
      return 'Choose the exact model from that provider, like claude-sonnet-4-0';
    case 'groq':
      return 'Choose the exact model from that provider, like llama-3.1-8b-instant';
    case 'ollama':
      return 'Choose the exact model from that provider, like gemma4:e4b';
    default:
      return 'Choose the exact model from that provider, like gpt-4o or gemma3:4b';
  }
}

function getProviderModelHelpText(
  providerType?: string,
  variant?: 'wizard' | 'settings'
): string | null {
  if (variant !== 'wizard') {
    return null;
  }

  switch (providerType) {
    case 'openai':
      return 'For OpenAI, this is usually something like gpt-4o or gpt-4.1-mini.';
    case 'azure-openai':
      return 'For Azure OpenAI, use the model or deployment name you created in Azure, such as gpt-4o.';
    case 'gemini':
    case 'google':
      return 'For Gemini, start with gemini-3.5-flash-lite if you want a current Google flash-lite model without a preview suffix.';
    case 'anthropic':
      return 'For Anthropic, this is usually something like claude-sonnet-4-0.';
    case 'groq':
      return 'For Groq, we recommend starting with llama-3.1-8b-instant because it currently has one of the best free-tier daily request limits among the chat models shown.';
    case 'ollama':
      return 'For Ollama, install Ollama first and start with gemma4:e4b.';
    default:
      return 'Pick the exact model your provider exposes. We will create the internal model id for you.';
  }
}

export const ModelDiscoverySelect: React.FC<{
  providerId: string;
  providerType?: string;
  modelName: string;
  availableModelNames: DiscoveredModelEntry[];
  discoveringModels: boolean;
  variant?: 'wizard' | 'settings';
  onModelNameChange(value: string): void;
  onRefresh(): void;
}> = ({
  providerId,
  providerType,
  modelName,
  availableModelNames,
  discoveringModels,
  variant = 'settings',
  onModelNameChange,
  onRefresh,
}) => {
  const helpText = getProviderModelHelpText(providerType, variant);
  const selectedModelDetails = getSelectedModelDetails(availableModelNames, modelName);

  return (
    <div>
      <div className="mb-1 flex items-center justify-between gap-2">
        <Label
          className="block"
          title="The actual model name from the provider (e.g., gpt-4o, claude-sonnet-4)"
        >
          Model Name
        </Label>
        <Button
          type="button"
          variant="secondary"
          onClick={onRefresh}
          disabled={!providerId || discoveringModels}
          title="Fetch available models from the selected provider"
        >
          {discoveringModels ? 'Loading...' : 'Refresh list'}
        </Button>
      </div>

      {availableModelNames.length > 0 ? (
        <Select
          title="Available models from your provider"
          value={modelName}
          onChange={(e) => onModelNameChange(e.target.value)}
        >
          <option value="">Select model...</option>
          {[...availableModelNames]
            .sort((a, b) => a.displayName.localeCompare(b.displayName) || a.id.localeCompare(b.id))
            .map((model) => (
              <option key={model.id} value={model.id}>
                {`${model.displayName}${formatCapabilityTags(model)}`}
              </option>
            ))}
        </Select>
      ) : (
        <Input
          value={modelName}
          onChange={(e) => onModelNameChange(e.target.value)}
          placeholder={getProviderModelPlaceholder(providerType, variant)}
          title="Exact model name from the provider"
        />
      )}
      {selectedModelDetails && (
        <p className="mt-1 text-xs text-muted-foreground">{selectedModelDetails}</p>
      )}
      {helpText && <p className="mt-1 text-xs text-muted-foreground">{helpText}</p>}
    </div>
  );
};

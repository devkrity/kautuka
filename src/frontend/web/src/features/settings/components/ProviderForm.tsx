import React, { useEffect, useState } from 'react';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select } from '@/components/ui/select';
import { settingsAdmin } from '../api/settingsAdmin';
import type { ProviderTypeInfo } from '../types/settingsTypes';
import type { ProviderFormState } from '../hooks/useProvidersTab';

const SECRET_INPUT_TYPE: React.HTMLInputTypeAttribute = ['pass', 'word'].join('');

function getProviderNamePlaceholder(providerType: string, variant: 'wizard' | 'settings'): string {
  if (variant !== 'wizard') {
    return 'e.g., ollama-local';
  }

  switch (providerType) {
    case 'openai':
      return 'Choose a short provider name, like openai-main';
    case 'openai-responses':
      return 'Choose a short provider name, like openai-responses-main';
    case 'openrouter':
      return 'Choose a short provider name, like openrouter-main';
    case 'azure-openai':
      return 'Choose a short provider name, like azure-openai-team';
    case 'gemini':
    case 'google':
      return 'Choose a short provider name, like gemini-main';
    case 'anthropic':
      return 'Choose a short provider name, like anthropic-main';
    case 'groq':
      return 'Choose a short provider name, like groq-fast';
    case 'ollama':
      return 'Choose a short provider name, like ollama-local';
    default:
      return 'Choose a short provider name, like my-provider';
  }
}

function getProviderNameHelpText(providerType: string, variant: 'wizard' | 'settings'): string {
  if (variant !== 'wizard') {
    return "Pick something short and memorable — you can't change this later.";
  }

  switch (providerType) {
    case 'openai':
      return "This is Kautuka's internal name for your OpenAI connection. Something like openai-main is enough.";
    case 'openai-responses':
      return "This is Kautuka's internal name for an OpenAI-compatible Responses endpoint. Something like openai-responses-main is enough.";
    case 'openrouter':
      return "This is Kautuka's internal name for your OpenRouter connection. Something like openrouter-main is enough.";
    case 'azure-openai':
      return "This is Kautuka's internal name for your Azure OpenAI connection. Something like azure-openai-team works well.";
    case 'gemini':
    case 'google':
      return "This is Kautuka's internal name for your Gemini connection. Something like gemini-main is enough.";
    case 'anthropic':
      return "This is Kautuka's internal name for your Anthropic connection. Something like anthropic-main is enough.";
    case 'groq':
      return "This is Kautuka's internal name for your Groq connection. Something like groq-fast is enough.";
    case 'ollama':
      return "This is Kautuka's internal name for your local Ollama connection. Something like ollama-local is enough.";
    default:
      return "This is Kautuka's internal name for the provider. Keep it short and easy to recognize.";
  }
}

export const ProviderForm: React.FC<{
  variant?: 'wizard' | 'settings';
  editingId: string;
  form: ProviderFormState;
  generatedProviderId?: string;
  result?: { success: boolean; message: string } | null;
  saving: boolean;
  cancelLabel?: string;
  onChange(next: ProviderFormState): void;
  onCancel(): void;
  onSave(): void;
}> = ({
  variant = 'settings',
  editingId,
  form,
  generatedProviderId = '',
  result = null,
  saving,
  cancelLabel = 'Cancel',
  onChange,
  onCancel,
  onSave,
}) => {
  const [providerTypes, setProviderTypes] = useState<ProviderTypeInfo[]>([]);

  useEffect(() => {
    settingsAdmin
      .listProviderTypes()
      .then((res) => setProviderTypes(res.data.providerTypes))
      .catch(() => {
        /* non-critical, form still functional */
      });
  }, []);

  const selectedType = providerTypes.find((t) => t.type === form.type);
  const needsApiKey = selectedType?.requiresApiKey ?? false;
  const supportsHttpTimeout = selectedType?.supportsHttpTimeoutSeconds ?? false;
  let apiKeyPlaceholder = 'Paste your API key';
  if (editingId) {
    apiKeyPlaceholder = 'Leave blank to keep existing key';
  } else if (form.type === 'gemini') {
    apiKeyPlaceholder = 'Paste your Google AI Studio key';
  } else if (form.type === 'groq') {
    apiKeyPlaceholder = 'Paste your Groq key';
  } else if (form.type === 'openrouter') {
    apiKeyPlaceholder = 'Paste your OpenRouter key';
  }
  let cardTitle = 'Create Provider';
  if (editingId) {
    cardTitle = `Edit Provider: ${editingId}`;
  } else if (variant === 'wizard') {
    cardTitle = 'Connect Provider';
  }

  const saveLabel = variant === 'wizard' ? 'Test and Continue' : 'Save Provider';
  const httpTimeoutValue = form.http_timeout_seconds || '30';

  return (
    <Card>
      <CardHeader>
        <CardTitle>{cardTitle}</CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        <div
          className={['grid grid-cols-1 gap-3', variant === 'wizard' ? '' : 'md:grid-cols-2'].join(
            ' '
          )}
        >
          {variant === 'wizard' ? (
            <div className="rounded-lg border border-border bg-muted/30 p-3 text-sm">
              <div className="font-medium text-foreground">Kautuka name</div>
              <div className="mt-1 text-muted-foreground">
                Kautuka will save this as{' '}
                <span className="font-medium text-foreground">{generatedProviderId}</span>.
              </div>
              <p className="mt-1 text-xs text-muted-foreground">
                You do not need to make up a system name here.
              </p>
            </div>
          ) : (
            <div>
              <Label
                className="mb-1 block"
                title="A short nickname you choose for this provider — you'll see this name throughout Kautuka"
              >
                Provider Name
              </Label>
              <Input
                value={form.id}
                disabled={!!editingId}
                onChange={(e) => onChange({ ...form, id: e.target.value })}
                placeholder={getProviderNamePlaceholder(form.type, variant)}
                title="Cannot be changed after creation"
              />
              {!editingId && (
                <p className="mt-1 text-xs text-muted-foreground">
                  {getProviderNameHelpText(form.type, variant)}
                </p>
              )}
            </div>
          )}
          <div>
            <Label
              className="mb-1 block"
              title="Choose where the AI models will run (local or cloud)"
            >
              Provider Type
            </Label>
            <Select
              title="The service hosting the AI models"
              value={form.type}
              onChange={(e) => {
                const nextType = e.target.value;
                let nextBaseUrl = form.base_url;
                if (nextType === 'ollama' && !nextBaseUrl.trim()) {
                  nextBaseUrl = 'http://localhost:11434';
                }

                onChange({ ...form, type: nextType, base_url: nextBaseUrl });
              }}
            >
              {providerTypes.map((pt) => (
                <option key={pt.type} value={pt.type}>
                  {pt.friendlyName}
                </option>
              ))}
            </Select>
            {variant === 'wizard' && (
              <p className="mt-1 text-xs text-muted-foreground">
                Choose where your helper's brain lives.
              </p>
            )}
          </div>
        </div>

        <div>
          <Label className="mb-1 block" title="The URL where the AI service is running">
            Base URL
          </Label>
          <Input
            value={form.base_url}
            onChange={(e) => onChange({ ...form, base_url: e.target.value })}
            placeholder={form.type === 'ollama' ? 'http://localhost:11434' : 'Leave blank to use the provider default'}
            title="Optional override for the provider endpoint. Leave blank to use the current catalog default when creating a provider."
          />
          <p className="mt-1 text-xs text-muted-foreground">
            {selectedType?.requiresBaseUrl
              ? 'This provider needs a base URL. Leave it blank only if you want Kautuka to copy the current catalog default when the provider is created.'
              : 'Optional override. Leave it blank to use the current catalog default when the provider is created.'}
          </p>
          {variant === 'wizard' && form.type === 'ollama' && (
            <p className="mt-1 text-xs text-muted-foreground">
              Install Ollama first from https://ollama.com/download, then keep the normal local
              address unless you changed it.
            </p>
          )}
        </div>

        {needsApiKey && (
          <div>
            <Label className="mb-1 block" title="Authentication secret for cloud AI services">
              API Key
            </Label>
            <Input
              type={SECRET_INPUT_TYPE}
              value={form.api_key}
              onChange={(e) => onChange({ ...form, api_key: e.target.value })}
              placeholder={apiKeyPlaceholder}
              title="Keep this secret! Never share with others"
            />
            {variant === 'wizard' && form.type === 'gemini' && (
              <p className="mt-1 text-xs text-muted-foreground">
                Easy Google path: use Google AI Studio and start with Gemini 3.1 Flash Lite. From
                the current free-tier table, it gives you much better prompt headroom than plain
                Gemini 3 Flash or Gemini 2.5 Flash Lite. If you still hit rate limits, switch to
                Groq.
              </p>
            )}
            {variant === 'wizard' && form.type === 'groq' && (
              <p className="mt-1 text-xs text-muted-foreground">
                If you want the Groq route, start with Llama 3.1 8B Instant. In the current Groq
                free plan table, it keeps a much stronger daily request budget than most of the
                other general chat options shown.
              </p>
            )}
          </div>
        )}

        {supportsHttpTimeout && (
          <div>
            <Label
              className="mb-1 block"
              title="Timeout for provider HTTP requests used during validation, model discovery, and run startup checks"
            >
              HTTP Timeout (seconds)
            </Label>
            <Input
              type="number"
              min="1"
              step="1"
              value={httpTimeoutValue}
              onChange={(e) =>
                onChange({
                  ...form,
                  http_timeout_seconds: e.target.value.trim() ? e.target.value : '30',
                })
              }
              title="Controls provider HTTP requests used during validation, model discovery, and runtime initialization"
            />
            <p className="mt-1 text-xs text-muted-foreground">
              Default is 30 seconds. This timeout applies when Kautuka tests the provider,
              fetches model lists, or initializes a run before model output starts. Lower values
              fail faster but can mark a slow provider unavailable; higher values wait longer
              before surfacing startup failures.
            </p>
          </div>
        )}

        {variant === 'wizard' && result && (
          <div
            className={[
              'rounded-lg border p-3 text-sm',
              result.success
                ? 'border-done/30 bg-done/10 text-done'
                : 'border-destructive/30 bg-destructive/10 text-destructive',
            ].join(' ')}
          >
            {result.message}
          </div>
        )}

        <div className="flex justify-end gap-2">
          <Button variant="secondary" onClick={onCancel}>
            {cancelLabel}
          </Button>
          <Button onClick={onSave} disabled={saving}>
            {saving ? 'Checking...' : saveLabel}
          </Button>
        </div>
      </CardContent>
    </Card>
  );
};

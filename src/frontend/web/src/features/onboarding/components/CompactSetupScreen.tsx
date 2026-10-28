import { Alert } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select } from '@/components/ui/select';
import { Textarea } from '@/components/ui/textarea';
import React from 'react';

export type BrainPresetOption = {
  id: string;
  label: string;
  summary: string;
  providerType: string;
  modelName: string;
  helperLinkLabel?: string;
  helperLinkHref?: string;
};

type CompactSetupScreenProps = {
  helperName: string;
  onHelperNameChange: (value: string) => void;
  systemPrompt: string;
  onSystemPromptChange: (value: string) => void;
  verbosityOptions: string[];
  verbosity: string;
  onVerbosityChange: (value: string) => void;
  styleOptions: string[];
  style: string;
  onStyleChange: (value: string) => void;
  toneOptions: string[];
  tone: string;
  onToneChange: (value: string) => void;
  brainPresets: BrainPresetOption[];
  selectedBrainPresetId: string | null;
  onSelectBrainPreset: (presetId: string) => void;
  manualBrainSelection: boolean;
  onManualBrainSelectionChange: (enabled: boolean) => void;
  providerType: string;
  onProviderTypeChange: (value: string) => void;
  providerBaseUrl: string;
  onProviderBaseUrlChange: (value: string) => void;
  providerApiCredential: string;
  onProviderApiCredentialChange: (value: string) => void;
  modelName: string;
  onModelNameChange: (value: string) => void;
  requiresApiKey: boolean;
  requiresBaseUrl: boolean;
  providerValidationMessage?: string | null;
  providerValidationSuccess?: boolean;
  runtimeNote: string;
  relationshipNote: string;
  saving: boolean;
  onBack: () => void;
  onSave: () => void;
};

export const CompactSetupScreen: React.FC<CompactSetupScreenProps> = ({
  helperName,
  onHelperNameChange,
  systemPrompt,
  onSystemPromptChange,
  verbosityOptions,
  verbosity,
  onVerbosityChange,
  styleOptions,
  style,
  onStyleChange,
  toneOptions,
  tone,
  onToneChange,
  brainPresets,
  selectedBrainPresetId,
  onSelectBrainPreset,
  manualBrainSelection,
  onManualBrainSelectionChange,
  providerType,
  onProviderTypeChange,
  providerBaseUrl,
  onProviderBaseUrlChange,
  providerApiCredential,
  onProviderApiCredentialChange,
  modelName,
  onModelNameChange,
  requiresApiKey,
  requiresBaseUrl,
  providerValidationMessage,
  providerValidationSuccess,
  runtimeNote,
  relationshipNote,
  saving,
  onBack,
  onSave,
}) => {
  const showConnectionDetails = manualBrainSelection || requiresApiKey || requiresBaseUrl;

  return (
    <div className="animate-in fade-in slide-in-from-right-8 duration-300 space-y-8">
      <div>
        <h2 className="text-2xl font-bold">Create your helper</h2>
        <p className="mt-1 max-w-2xl text-sm text-muted-foreground">
          Shape the helper first, then choose its AI brain.
        </p>
      </div>

      <div className="rounded-xl border border-border bg-card p-6 shadow-sm">
        <div className="border-b border-border pb-4">
          <div className="text-sm font-medium text-foreground">Agent</div>
          <p className="mt-1 text-sm text-muted-foreground">
            You can adjust the name, prompt, and personality now.
          </p>
        </div>

        <div className="mt-5 grid gap-5">
          <div>
            <Label className="mb-2 block">Helper name</Label>
            <Input
              value={helperName}
              onChange={(event) => onHelperNameChange(event.target.value)}
              placeholder="Desk Assistant"
            />
          </div>

          <div>
            <Label className="mb-2 block">Instructions</Label>
            <Textarea
              value={systemPrompt}
              onChange={(event) => onSystemPromptChange(event.target.value)}
              placeholder="Tell the helper what it should focus on first."
              rows={5}
            />
          </div>

          <div className="grid gap-4 md:grid-cols-3">
            <div>
              <Label className="mb-2 block">Verbosity</Label>
              <Select value={verbosity} onChange={(event) => onVerbosityChange(event.target.value)}>
                {verbosityOptions.map((option) => (
                  <option key={option} value={option}>
                    {option}
                  </option>
                ))}
              </Select>
            </div>
            <div>
              <Label className="mb-2 block">Style</Label>
              <Select value={style} onChange={(event) => onStyleChange(event.target.value)}>
                {styleOptions.map((option) => (
                  <option key={option} value={option}>
                    {option}
                  </option>
                ))}
              </Select>
            </div>
            <div>
              <Label className="mb-2 block">Tone</Label>
              <Select value={tone} onChange={(event) => onToneChange(event.target.value)}>
                {toneOptions.map((option) => (
                  <option key={option} value={option}>
                    {option}
                  </option>
                ))}
              </Select>
            </div>
          </div>
        </div>
      </div>

      <div className="rounded-xl border border-border bg-card p-6 shadow-sm">
        <div className="flex flex-col gap-2 border-b border-border pb-4 md:flex-row md:items-start md:justify-between">
          <div>
            <div className="text-sm font-medium text-foreground">AI brain</div>
            <p className="text-sm text-muted-foreground">Choose a ready-made brain or set it up manually.</p>
          </div>
          <div className="text-xs text-muted-foreground">{runtimeNote}</div>
        </div>

        <div className="mt-5 grid gap-5">
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
            {brainPresets.map((preset) => (
              <button
                key={preset.id}
                type="button"
                onClick={() => {
                  onManualBrainSelectionChange(false);
                  onSelectBrainPreset(preset.id);
                }}
                className={[
                  'rounded-xl border p-5 text-left shadow-sm transition-colors',
                  selectedBrainPresetId === preset.id && !manualBrainSelection
                    ? 'border-primary bg-primary/5'
                    : 'border-border bg-card hover:border-primary/50 hover:bg-primary/5',
                ].join(' ')}
              >
                <div className="text-base font-semibold">{preset.label}</div>
                <p className="mt-2 text-sm text-muted-foreground">{preset.summary}</p>
                {preset.helperLinkHref && preset.helperLinkLabel && (
                  <a
                    href={preset.helperLinkHref}
                    target="_blank"
                    rel="noreferrer"
                    onClick={(event) => event.stopPropagation()}
                    className="mt-3 inline-flex text-xs font-medium text-primary underline-offset-4 hover:underline"
                  >
                    {preset.helperLinkLabel}
                  </a>
                )}
              </button>
            ))}
          </div>

          <div className="flex items-center justify-between gap-3 rounded-lg border border-border bg-muted/20 px-4 py-3 text-sm">
            <span className="text-muted-foreground">{relationshipNote}</span>
            <Button
              type="button"
              variant={manualBrainSelection ? 'default' : 'secondary'}
              onClick={() => onManualBrainSelectionChange(!manualBrainSelection)}
            >
              {manualBrainSelection ? 'Using manual setup' : 'Set it up manually'}
            </Button>
          </div>

          {showConnectionDetails && (
            <div className="grid gap-5 md:grid-cols-2">
              {manualBrainSelection ? (
                <>
                  <div>
                    <Label className="mb-2 block">Provider</Label>
                    <Select value={providerType} onChange={(event) => onProviderTypeChange(event.target.value)}>
                      <option value="gemini">Google Gemini</option>
                      <option value="ollama">Ollama</option>
                      <option value="openrouter">OpenRouter</option>
                      <option value="openai">OpenAI</option>
                      <option value="anthropic">Anthropic</option>
                      <option value="groq">Groq</option>
                    </Select>
                  </div>

                  <div>
                    <Label className="mb-2 block">Model</Label>
                    <Input value={modelName} onChange={(event) => onModelNameChange(event.target.value)} />
                  </div>
                </>
              ) : (
                <>
                  <div>
                    <Label className="mb-2 block">Provider</Label>
                    <div className="rounded-lg border border-border bg-muted/40 px-4 py-3 text-sm text-foreground">
                      {providerType}
                    </div>
                  </div>

                  <div>
                    <Label className="mb-2 block">Model</Label>
                    <div className="rounded-lg border border-border bg-muted/40 px-4 py-3 text-sm text-foreground">
                      {modelName}
                    </div>
                  </div>
                </>
              )}

              {requiresApiKey && (
                <div>
                  <Label className="mb-2 block">API key</Label>
                  <Input
                    type={['pass', 'word'].join('')}
                    value={providerApiCredential}
                    onChange={(event) => onProviderApiCredentialChange(event.target.value)}
                    placeholder="Paste your provider key"
                  />
                </div>
              )}

              {requiresBaseUrl && (
                <div>
                  <Label className="mb-2 block">Base URL</Label>
                  <Input
                    value={providerBaseUrl}
                    onChange={(event) => onProviderBaseUrlChange(event.target.value)}
                    placeholder={providerType === 'ollama' ? 'http://localhost:11434' : 'Enter the provider URL'}
                  />
                </div>
              )}
            </div>
          )}

          {providerValidationMessage && (
            <Alert variant={providerValidationSuccess ? 'success' : 'destructive'}>
              {providerValidationMessage}
            </Alert>
          )}
        </div>
      </div>

      <div className="flex justify-between gap-3">
        <Button variant="secondary" onClick={onBack} disabled={saving}>
          Back
        </Button>
        <Button onClick={onSave} disabled={saving}>
          {saving ? 'Saving…' : 'Save helper'}
        </Button>
      </div>
    </div>
  );
};
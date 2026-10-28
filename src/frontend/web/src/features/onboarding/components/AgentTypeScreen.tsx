import { Button } from '@/components/ui/button';
import React from 'react';

export type AgentTypePreset = {
  id: string;
  label: string;
  description: string;
  suggestedName: string;
  prompt: string;
  style: string;
  tone: string;
  verbosity: string;
};

type AgentTypeScreenProps = {
  presets: AgentTypePreset[];
  selectedPresetId: string | null;
  onSelectPreset: (presetId: string | null) => void;
  onBack: () => void;
  onContinue: () => void;
};

export const AgentTypeScreen: React.FC<AgentTypeScreenProps> = ({
  presets,
  selectedPresetId,
  onSelectPreset,
  onBack,
  onContinue,
}) => {
  return (
    <div className="animate-in fade-in slide-in-from-right-8 duration-300 space-y-8">
      <div>
        <h2 className="text-2xl font-bold">Choose your helper</h2>
        <p className="mt-1 max-w-3xl text-sm text-muted-foreground">
          Pick a starting point. You can change the name, prompt, and personality on the next step.
        </p>
      </div>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
        {presets.map((preset) => (
          <button
            key={preset.id}
            type="button"
            onClick={() => onSelectPreset(preset.id)}
            className={[
              'rounded-xl border p-5 text-left shadow-sm transition-colors',
              selectedPresetId === preset.id
                ? 'border-primary bg-primary/5'
                : 'border-border bg-card hover:border-primary/50 hover:bg-primary/5',
            ].join(' ')}
          >
            <div className="text-base font-semibold">{preset.label}</div>
            <p className="mt-2 text-sm text-muted-foreground">{preset.description}</p>
            <div className="mt-3 flex flex-wrap gap-2 text-xs text-muted-foreground">
              <span className="rounded-full border border-border px-2 py-1">{preset.verbosity}</span>
              <span className="rounded-full border border-border px-2 py-1">{preset.style}</span>
              <span className="rounded-full border border-border px-2 py-1">{preset.tone}</span>
            </div>
          </button>
        ))}

        <button
          type="button"
          onClick={() => onSelectPreset(null)}
          className={[
            'rounded-xl border p-5 text-left shadow-sm transition-colors',
            selectedPresetId === null
              ? 'border-primary bg-primary/5'
              : 'border-border bg-card hover:border-primary/50 hover:bg-primary/5',
          ].join(' ')}
        >
          <div className="text-base font-semibold">Manual setup</div>
          <p className="mt-2 text-sm text-muted-foreground">
            Start from scratch and define the helper yourself.
          </p>
          <div className="mt-3 text-xs text-muted-foreground">
            Full control over prompt, style, tone, provider, and model.
          </div>
        </button>
      </div>

      <div className="flex justify-between gap-3">
        <Button variant="secondary" onClick={onBack}>
          Back
        </Button>
        <Button onClick={onContinue}>Continue</Button>
      </div>
    </div>
  );
};
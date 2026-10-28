import React from 'react';
import { Boxes } from 'lucide-react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { ConfirmDeleteButton } from './SettingsShared';
import { CapabilityAdvisory } from './CapabilityAdvisory';
import type { ModelEntry } from '../types/settingsTypes';

export const ModelCard: React.FC<{
  model: ModelEntry;
  probingId: string | null;
  providerLabel(providerId: string): string;
  onProbe(modelId: string): void;
  onEdit(model: ModelEntry): void;
  onDelete(modelId: string): void;
}> = ({ model, probingId, providerLabel, onProbe, onEdit, onDelete }) => (
  <Card className="card-interactive">
    <CardHeader>
      <div className="flex items-start justify-between gap-3">
        <div>
          <div className="mb-2 inline-flex items-center gap-2">
            <Boxes className="h-4 w-4 text-primary" />
            <Badge variant="info">Model</Badge>
          </div>
          <CardTitle className="text-xl tracking-tight">{model.id}</CardTitle>
          <CardDescription>{model.modelName}</CardDescription>
        </div>
        <div className="flex gap-2">
          <Button
            variant="secondary"
            onClick={() => onProbe(model.id)}
            disabled={probingId === model.id}
            title="Run a quick capability check on this model"
          >
            {probingId === model.id ? 'Checking...' : 'Check'}
          </Button>
          <Button variant="secondary" onClick={() => onEdit(model)}>
            Edit
          </Button>
          <ConfirmDeleteButton
            title={`Delete model ${model.id}?`}
            description="This action is blocked if agents still reference this model."
            onConfirm={() => onDelete(model.id)}
          />
        </div>
      </div>
    </CardHeader>
    <CardContent className="space-y-1 pt-0 text-sm text-muted-foreground">
      <p>Provider: {providerLabel(model.providerId)}</p>
      {typeof model.settings?.contextWindowTokens === 'number' && (
        <p>Context window: {model.settings.contextWindowTokens.toLocaleString()} tokens</p>
      )}
      {typeof model.settings?.autoCompactThresholdPercent === 'number' && (
        <p>Auto-compact threshold: {model.settings.autoCompactThresholdPercent}%</p>
      )}
      {typeof model.settings?.custom_output_text === 'string' &&
        model.settings.custom_output_text.trim() !== '' && <p>Custom test output configured</p>}
      <CapabilityAdvisory capabilities={model.capabilities} />
    </CardContent>
  </Card>
);

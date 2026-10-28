import React from 'react';
import { Cloud, Server } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { ConfirmDeleteButton } from './SettingsShared';
import { providerChip } from '../hooks/useProvidersTab';
import type { ProviderEntry } from '../types/settingsTypes';

const PROVIDER_KIND_TEST = ['te', 'st'].join('');

export const ProviderCard: React.FC<{
  provider: ProviderEntry;
  onEdit(provider: ProviderEntry): void;
  onDelete(id: string): void;
}> = ({ provider, onEdit, onDelete }) => {
  const chip = providerChip(provider.type);
  const isLocal = provider.type === 'ollama' || provider.type === PROVIDER_KIND_TEST;

  return (
    <Card className="card-interactive">
      <CardHeader>
        <div className="flex items-start justify-between gap-3">
          <div>
            <div className="mb-2 inline-flex items-center gap-2">
              {isLocal ? (
                <Server className="h-4 w-4 text-primary" />
              ) : (
                <Cloud className="h-4 w-4 text-primary" />
              )}
              <span className={chip.className}>{chip.label}</span>
            </div>
            <CardTitle className="text-2xl lowercase tracking-tight">{provider.id}</CardTitle>
            <CardDescription>{provider.type} provider</CardDescription>
          </div>
          <div className="flex gap-2">
            <Button variant="secondary" onClick={() => onEdit(provider)}>
              Edit
            </Button>
            <ConfirmDeleteButton
              title={`Delete provider ${provider.id}?`}
              description="This action is blocked if models or agents depend on it."
              onConfirm={() => onDelete(provider.id)}
            />
          </div>
        </div>
      </CardHeader>
      <CardContent className="space-y-1 pt-0 text-sm text-muted-foreground">
        <p>Base URL: {String(provider.settings?.base_url ?? '') || 'n/a'}</p>
        <p>API key: {provider.hasApiKey ? 'set' : 'not set'}</p>
        <p>HTTP timeout: {String(provider.settings?.http_timeout_seconds ?? '30')} seconds</p>
      </CardContent>
    </Card>
  );
};

import React from 'react';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';

interface PersonaDefinitionsSectionProps {
  companyName?: string;
  styles: string;
  tones: string;
  verbosityLevels: string;
  saving?: boolean;
  showSaveAction?: boolean;
  onCompanyNameChange?: (value: string) => void;
  onStylesChange: (value: string) => void;
  onTonesChange: (value: string) => void;
  onVerbosityLevelsChange: (value: string) => void;
  onSave?: () => void;
}

export function PersonaDefinitionsSection({
  companyName,
  styles,
  tones,
  verbosityLevels,
  saving = false,
  showSaveAction = true,
  onCompanyNameChange,
  onStylesChange,
  onTonesChange,
  onVerbosityLevelsChange,
  onSave,
}: Readonly<PersonaDefinitionsSectionProps>) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Persona Definitions</CardTitle>
        <CardDescription>
          Configure styles, tones, verbosity levels, and optional office metadata rules used by
          agent persona profiles. Values are comma-separated.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
          {typeof companyName === 'string' && onCompanyNameChange ? (
            <div className="md:col-span-2">
              <label
                className="mb-1 block text-xs text-muted-foreground"
                htmlFor="persona-company-name"
              >
                Company Name (optional)
              </label>
              <Input
                id="persona-company-name"
                value={companyName}
                onChange={(e) => onCompanyNameChange(e.target.value)}
                placeholder="Contoso"
              />
            </div>
          ) : null}
          <div>
            <label className="mb-1 block text-xs text-muted-foreground" htmlFor="persona-styles">
              Styles
            </label>
            <Input
              id="persona-styles"
              value={styles}
              onChange={(e) => onStylesChange(e.target.value)}
              placeholder="professional, helper, analytical"
            />
          </div>
          <div>
            <label className="mb-1 block text-xs text-muted-foreground" htmlFor="persona-tones">
              Tones
            </label>
            <Input
              id="persona-tones"
              value={tones}
              onChange={(e) => onTonesChange(e.target.value)}
              placeholder="neutral, friendly, formal"
            />
          </div>
          <div>
            <label className="mb-1 block text-xs text-muted-foreground" htmlFor="persona-verbosity">
              Verbosity Levels
            </label>
            <Input
              id="persona-verbosity"
              value={verbosityLevels}
              onChange={(e) => onVerbosityLevelsChange(e.target.value)}
              placeholder="concise, normal, detailed"
            />
          </div>
        </div>

        {showSaveAction ? (
          <div className="flex justify-end">
            <Button variant="secondary" disabled={saving} onClick={onSave}>
              {saving ? 'Saving Persona Definitions...' : 'Save Persona Definitions'}
            </Button>
          </div>
        ) : null}
      </CardContent>
    </Card>
  );
}

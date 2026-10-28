import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import React from 'react';
import type { KautukaInfrastructureSettingsEntry } from '../types/settingsTypes';

export const WorkspaceAndSandboxSection: React.FC<{
  kautukaSettings: KautukaInfrastructureSettingsEntry | null;
  sandboxImage: string;
  sandboxCpus: string;
  sandboxMemory: string;
  sandboxTimeout: string;
  hideDocumentIngestion?: boolean;
  documentIngestionEnabled: boolean;
  internalParserConnectorId: string;
  internalParserToolName: string;
  parserTimeoutSeconds: string;
  parseRequiredExtensions: string;
  pdfImageFallbackEnabled: boolean;
  visionRouteAllowed: boolean;
  onSandboxImageChange(value: string): void;
  onSandboxCpusChange(value: string): void;
  onSandboxMemoryChange(value: string): void;
  onSandboxTimeoutChange(value: string): void;
  onDocumentIngestionEnabledChange(value: boolean): void;
  onInternalParserConnectorIdChange(value: string): void;
  onInternalParserToolNameChange(value: string): void;
  onParserTimeoutSecondsChange(value: string): void;
  onParseRequiredExtensionsChange(value: string): void;
  onPdfImageFallbackEnabledChange(value: boolean): void;
  onVisionRouteAllowedChange(value: boolean): void;
}> = ({
  kautukaSettings,
  sandboxImage,
  sandboxCpus,
  sandboxMemory,
  sandboxTimeout,
  hideDocumentIngestion = false,
  documentIngestionEnabled,
  internalParserConnectorId,
  internalParserToolName,
  parserTimeoutSeconds,
  parseRequiredExtensions,
  pdfImageFallbackEnabled,
  visionRouteAllowed,
  onSandboxImageChange,
  onSandboxCpusChange,
  onSandboxMemoryChange,
  onSandboxTimeoutChange,
  onDocumentIngestionEnabledChange,
  onInternalParserConnectorIdChange,
  onInternalParserToolNameChange,
  onParserTimeoutSecondsChange,
  onParseRequiredExtensionsChange,
  onPdfImageFallbackEnabledChange,
  onVisionRouteAllowedChange,
}) => (
  <Card className="card-solid">
    <CardHeader>
      <CardTitle>Workspace and Sandbox</CardTitle>
      <CardDescription>
        Review the effective workspace location and configure the Podman defaults the runtime will
        validate and use.
      </CardDescription>
    </CardHeader>
    <CardContent className="space-y-4">
      <div className="grid gap-4 md:grid-cols-2">
        <div className="space-y-2">
          <Label htmlFor="sandbox-image">Default sandbox image</Label>
          <Input
            id="sandbox-image"
            value={sandboxImage}
            onChange={(e) => onSandboxImageChange(e.target.value)}
            placeholder="kautuka-base:latest"
          />
        </div>

        <div className="space-y-2">
          <Label htmlFor="sandbox-cpus">Default CPU limit</Label>
          <Input
            id="sandbox-cpus"
            type="number"
            min="1"
            step="1"
            value={sandboxCpus}
            onChange={(e) => onSandboxCpusChange(e.target.value)}
          />
        </div>

        <div className="space-y-2">
          <Label htmlFor="sandbox-memory">Default memory limit</Label>
          <Input
            id="sandbox-memory"
            value={sandboxMemory}
            onChange={(e) => onSandboxMemoryChange(e.target.value)}
            placeholder="4g"
          />
        </div>

        <div className="space-y-2">
          <Label htmlFor="sandbox-timeout">Default timeout seconds</Label>
          <Input
            id="sandbox-timeout"
            type="number"
            min="1"
            step="1"
            value={sandboxTimeout}
            onChange={(e) => onSandboxTimeoutChange(e.target.value)}
          />
        </div>
      </div>

      <div className="grid grid-cols-1 gap-3 text-sm text-muted-foreground md:grid-cols-2">
        <div>
          <p className="font-medium text-foreground">Effective Workspace Root</p>
          <p>{kautukaSettings?.workspace?.baseRoot ?? 'Not resolved yet'}</p>
          <p className="mt-1 text-xs text-muted-foreground">
            Workspace placement is managed automatically by the active Kautuka runtime mode.
          </p>
        </div>
        <div>
          <p className="font-medium text-foreground">Default Sandbox Image</p>
          <p>{kautukaSettings?.sandbox?.image ?? sandboxImage}</p>
        </div>
      </div>

      {!hideDocumentIngestion && (
        <div className="rounded border border-border p-4">
          <p className="text-sm font-medium text-foreground">
            Document Ingestion (Internal MCP Parser)
          </p>
          <p className="mt-1 text-xs text-muted-foreground">
            Controls parse-required attachment ingestion via a container MCP connector before content
            is sent to the model.
          </p>

          <div className="mt-4 grid gap-4 md:grid-cols-2">
            <label className="flex items-center gap-2 text-sm">
              <input
                type="checkbox"
                checked={documentIngestionEnabled}
                onChange={(e) => onDocumentIngestionEnabledChange(e.target.checked)}
              />
              Enable document parsing
            </label>

            <label className="flex items-center gap-2 text-sm">
              <input
                type="checkbox"
                checked={pdfImageFallbackEnabled}
                onChange={(e) => onPdfImageFallbackEnabledChange(e.target.checked)}
              />
              Enable PDF image fallback
            </label>

            <label className="flex items-center gap-2 text-sm md:col-span-2">
              <input
                type="checkbox"
                checked={visionRouteAllowed}
                onChange={(e) => onVisionRouteAllowedChange(e.target.checked)}
              />
              Allow vision-route fallback for parsed PDFs
            </label>

            <div className="space-y-2">
              <Label htmlFor="document-ingestion-connector-id">Connector ID</Label>
              <Input
                id="document-ingestion-connector-id"
                value={internalParserConnectorId}
                onChange={(e) => onInternalParserConnectorIdChange(e.target.value)}
                placeholder="markitdown"
              />
            </div>

            <div className="space-y-2">
              <Label htmlFor="document-ingestion-tool-name">Tool Name</Label>
              <Input
                id="document-ingestion-tool-name"
                value={internalParserToolName}
                onChange={(e) => onInternalParserToolNameChange(e.target.value)}
                placeholder="convert_to_markdown"
              />
            </div>

            <div className="space-y-2">
              <Label htmlFor="document-ingestion-timeout">Parser Timeout (seconds)</Label>
              <Input
                id="document-ingestion-timeout"
                type="number"
                min="1"
                step="1"
                value={parserTimeoutSeconds}
                onChange={(e) => onParserTimeoutSecondsChange(e.target.value)}
              />
            </div>

            <div className="space-y-2 md:col-span-2">
              <Label htmlFor="document-ingestion-extensions">Parse-Required Extensions</Label>
              <Input
                id="document-ingestion-extensions"
                value={parseRequiredExtensions}
                onChange={(e) => onParseRequiredExtensionsChange(e.target.value)}
                placeholder=".pdf,.docx,.pptx,.xlsx"
              />
            </div>
          </div>
        </div>
      )}
    </CardContent>
  </Card>
);

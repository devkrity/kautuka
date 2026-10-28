import { CatalogCard, CatalogIcon } from '@/components/layout/CatalogCard';
import { Alert } from '@/components/ui/alert';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { AlertCircle, CheckCircle2, FileArchive, Upload } from 'lucide-react';
import React, { useState } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import type { SkillImportResult, SkillInspection } from '../schemas/skillSchema';

export interface SkillImportOptions {
  sourceMetadataJson?: string;
}

interface SkillImportPanelProps {
  canImport: boolean;
  validating: boolean;
  importing: boolean;
  preview: SkillImportResult | null;
  errorMessage: string | null;
  onValidate: (file: File, options: SkillImportOptions) => Promise<void>;
  onImport: (file: File, options: SkillImportOptions) => Promise<void>;
}

const DryRunSkillInspection: React.FC<{
  inspection: SkillInspection;
  title: string;
  subtitle: string;
}> = ({ inspection, title, subtitle }) => (
  <CatalogCard
    icon={
      <CatalogIcon>
        <CheckCircle2 className="h-5 w-5 text-done" />
      </CatalogIcon>
    }
    title={title}
    badges={
      <>
        <Badge variant="success">Ready</Badge>
        <Badge variant="muted">Dry Run</Badge>
        {inspection.resourcePaths.length > 0 && <Badge variant="muted">Resources</Badge>}
        {inspection.scriptPaths.length > 0 && <Badge variant="muted">Scripts</Badge>}
      </>
    }
    description={inspection.skill.id}
    meta={<p className="mt-1 text-sm text-muted-foreground">{subtitle}</p>}
  >

    <div className="mb-4 grid gap-3 text-sm md:grid-cols-2">
      <div className="rounded-lg border border-border/70 bg-muted/30 p-3">
        <div className="section-label mb-1">
          Description
        </div>
        <p className="text-sm text-foreground">{inspection.skill.description}</p>
      </div>
      <div className="rounded-lg border border-border/70 bg-muted/30 p-3 text-muted-foreground">
        <div className="section-label mb-1">
          Package Summary
        </div>
        <p>Resources: {inspection.resourcePaths.length}</p>
        <p>Scripts: {inspection.scriptPaths.length}</p>
        <p>Status: {inspection.skill.status}</p>
      </div>
    </div>

    <div className="grid gap-4 xl:grid-cols-[2fr,1fr]">
      <div className="rounded-xl border border-border bg-card p-4 shadow-sm">
        <div className="mb-3 text-sm font-medium text-foreground">Instructions</div>
        <div className="prose prose-sm max-w-none dark:prose-invert">
          <ReactMarkdown remarkPlugins={[remarkGfm]}>
            {inspection.instructionsMarkdown}
          </ReactMarkdown>
        </div>
      </div>
      <div className="space-y-4">
        <div className="rounded-xl border border-border bg-card p-4 shadow-sm">
          <div className="mb-3 text-sm font-medium text-foreground">Resources</div>
          {inspection.resourcePaths.length > 0 ? (
            <ul className="space-y-1 text-sm text-muted-foreground">
              {inspection.resourcePaths.map((resourcePath) => (
                <li key={resourcePath}>{resourcePath}</li>
              ))}
            </ul>
          ) : (
            <p className="text-sm text-muted-foreground">No packaged resources.</p>
          )}
        </div>
        <div className="rounded-xl border border-border bg-card p-4 shadow-sm">
          <div className="mb-3 text-sm font-medium text-foreground">Scripts</div>
          {inspection.scriptPaths.length > 0 ? (
            <ul className="space-y-1 text-sm text-muted-foreground">
              {inspection.scriptPaths.map((scriptPath) => (
                <li key={scriptPath}>{scriptPath}</li>
              ))}
            </ul>
          ) : (
            <p className="text-sm text-muted-foreground">No packaged scripts.</p>
          )}
        </div>
      </div>
    </div>
  </CatalogCard>
);

export const SkillImportPanel: React.FC<SkillImportPanelProps> = ({
  canImport,
  validating,
  importing,
  preview,
  errorMessage,
  onValidate,
  onImport,
}) => {
  const [file, setFile] = useState<File | null>(null);
  const [sourceMetadataJson, setSourceMetadataJson] = useState('');

  const options: SkillImportOptions = {
    sourceMetadataJson,
  };

  const selectedFile = file;

  return (
    <div className="workspace-block space-y-4">
      <div>
        <p className="section-label mb-1">
          Skill Packages
        </p>
        <h2 className="text-2xl font-extrabold tracking-tight text-foreground">
          Validate and Import
        </h2>
        <p className="mt-1 text-sm text-muted-foreground">
          Upload a ZIP package with a root SKILL.md, then assign imported skills from agent
          settings.
        </p>
      </div>

      <div className="grid gap-3 md:grid-cols-2">
        <div>
          <label className="mb-1 block text-xs text-muted-foreground" htmlFor="skill-package-file">
            ZIP package
          </label>
          <Input
            id="skill-package-file"
            type="file"
            accept=".zip,application/zip"
            onChange={(event) => setFile(event.target.files?.[0] ?? null)}
          />
        </div>
      </div>

      <div>
        <label
          className="mb-1 block text-xs text-muted-foreground"
          htmlFor="skill-package-source-metadata"
        >
          Source metadata JSON
        </label>
        <Textarea
          id="skill-package-source-metadata"
          rows={3}
          value={sourceMetadataJson}
          onChange={(event) => setSourceMetadataJson(event.target.value)}
          placeholder='{"publisher":"platform","origin":"internal"}'
        />
      </div>

      <div className="flex flex-wrap items-center gap-3">
        <Button
          variant="secondary"
          onClick={() => {
            if (!selectedFile) return;
            void onValidate(selectedFile, options);
          }}
          disabled={!canImport || !selectedFile || validating || importing}
        >
          <FileArchive className="mr-2 h-4 w-4" />
          {validating ? 'Validating...' : 'Validate Package'}
        </Button>
        <Button
          onClick={() => {
            if (!selectedFile) return;
            void onImport(selectedFile, options);
          }}
          disabled={!canImport || !selectedFile || importing || validating}
        >
          <Upload className="mr-2 h-4 w-4" />
          {importing ? 'Importing...' : 'Import Skill'}
        </Button>
      </div>

      {errorMessage && (
        <Alert variant="destructive">
          <div className="flex items-start gap-2">
            <AlertCircle className="mt-0.5 h-4 w-4" />
            <span>{errorMessage}</span>
          </div>
        </Alert>
      )}

      {!canImport && (
        <Alert variant="attention">
          You do not have permission to import skills. Ask an administrator to grant import access.
        </Alert>
      )}

      {preview?.type === 'single' && (
        <DryRunSkillInspection
          inspection={preview.single}
          title="Dry-Run Inspection"
          subtitle="This package passed validation and has not been imported yet."
        />
      )}

      {preview?.type === 'bundle' && (
        <div className="space-y-4">
          <div className="rounded-xl border border-border bg-card p-4 shadow-sm">
            <div className="mb-2 flex items-center gap-2 text-sm font-medium text-foreground">
              <CheckCircle2 className="h-4 w-4" /> Dry-Run Bundle Inspection
            </div>
            <p className="text-sm text-muted-foreground">
              This bundle passed validation and contains {preview.bundle.skills.length} importable
              skills.
            </p>
          </div>

          {preview.bundle.skills.map((inspection) => (
            <DryRunSkillInspection
              key={inspection.skill.id}
              inspection={inspection}
              title={inspection.skill.name}
              subtitle="Bundle skill preview before import."
            />
          ))}
        </div>
      )}
    </div>
  );
};

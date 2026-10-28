import { Alert } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogTitle } from '@/components/ui/dialog';
import { SegmentedControl } from '@/components/ui/segmented-control';
import { AlertCircle, CheckCircle2, Loader2, Upload, X } from 'lucide-react';
import React, { useState } from 'react';
import { importMcpbConnectorBundle, saveConnector } from '../api/connectors';
import type { Connector, ConnectorMcpbImportResult } from '../schemas/connectorSchema';
import type { ConnectorSavePayload } from '../types/connectorDraft';
import {
    IMPORT_EXAMPLE,
    parseMcpServersJson,
    type ImportPhase,
    type ImportResult,
    type McpImportEntry,
} from '../types/mcpImport';

interface ImportModalProps {
  existingIds: Set<string>;
  onImported: (saved: Connector) => void;
  onClose: () => void;
}

export const ImportModal: React.FC<ImportModalProps> = ({ existingIds, onImported, onClose }) => {
  const [mode, setMode] = useState<'json' | 'mcpb'>('json');
  const [raw, setRaw] = useState('');
  const [phase, setPhase] = useState<ImportPhase>('paste');
  const [results, setResults] = useState<ImportResult[]>([]);
  const [mcpbFile, setMcpbFile] = useState<File | null>(null);
  const [mcpbResult, setMcpbResult] = useState<ConnectorMcpbImportResult | null>(null);
  const [mcpbError, setMcpbError] = useState<string | null>(null);

  const { entries, error: parseError } = raw.trim()
    ? parseMcpServersJson(raw)
    : { entries: [], error: undefined };

  const handleImport = async () => {
    if (mode === 'mcpb') {
      await handleMcpbImport();
      return;
    }

    setPhase('importing');
    const res: ImportResult[] = [];
    for (const entry of entries) {
      try {
        const payload: ConnectorSavePayload = {
          name: entry.name,
          description: '',
          iconUrl: '',
          presetId: null,
          transport: entry.transport,
          httpUrl: entry.transport === 'http' ? entry.url : null,
          localCommand: entry.transport === 'local' ? entry.command || null : null,
          localArgs: entry.transport === 'local' ? entry.args : [],
          containerImage: null,
          containerCommand: null,
          containerArgs: [],
          containerSidecarName: null,
          bundlePath: null,
          serverType: null,
          readinessTimeoutSeconds: null,
          jitCooldownSeconds: null,
          healthcheckUrl: null,
          healthcheckIntervalMilliseconds: null,
          mcpProtocolVersion: null,
          envKeys: entry.envKeys,
          enabled: true,
          toolCount: null,
          toolNames: null,
        };
        const saved = await saveConnector(entry.id, payload);
        onImported(saved);
        res.push({ entry, success: true });
      } catch (err) {
        const msg = err instanceof Error ? err.message : 'Failed to save.';
        res.push({ entry, success: false, error: msg });
      }
    }
    setResults(res);
    setPhase('done');
  };

  const handleMcpbImport = async () => {
    if (!mcpbFile) {
      setMcpbError('Select a .mcpb bundle file first.');
      return;
    }

    if (!mcpbFile.name.toLowerCase().endsWith('.mcpb')) {
      setMcpbError('Only .mcpb bundle files are supported.');
      return;
    }

    setMcpbError(null);
    setPhase('importing');
    try {
      const response = await importMcpbConnectorBundle(mcpbFile);
      if (response.connector) {
        onImported(response.connector);
      }
      setMcpbResult(response);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'MCPB import failed.';
      setMcpbResult({
        outcome: 'failed',
        message,
        serverId: '',
        manifestVersion: '',
        serverType: '',
        connector: null,
      });
    }
    setPhase('done');
  };

  const resetForMode = (nextMode: 'json' | 'mcpb') => {
    if (phase === 'importing') {
      return;
    }

    setMode(nextMode);
    setPhase('paste');
    setResults([]);
    setMcpbResult(null);
    setMcpbError(null);
  };

  const formatFileSize = (size: number): string => {
    if (size < 1024) {
      return `${size} B`;
    }

    if (size < 1024 * 1024) {
      return `${(size / 1024).toFixed(1)} KB`;
    }

    return `${(size / (1024 * 1024)).toFixed(1)} MB`;
  };

  return (
    <Dialog
      open
      onOpenChange={(next) => {
        if (!next && phase !== 'importing') {
          onClose();
        }
      }}
    >
      <DialogContent
        hideClose
        className="max-w-xl gap-0 overflow-hidden p-0"
        onPointerDownOutside={(event) => {
          if (phase === 'importing') {
            event.preventDefault();
          }
        }}
        onEscapeKeyDown={(event) => {
          if (phase === 'importing') {
            event.preventDefault();
          }
        }}
      >
        <div className="flex items-center justify-between border-b border-border px-4 py-3">
          <DialogTitle className="flex items-center gap-2 text-sm font-semibold">
            <Upload className="h-4 w-4" /> Import from mcpServers JSON
          </DialogTitle>
          {phase !== 'importing' && (
            <Button type="button" variant="ghost" size="icon-sm" onClick={onClose} title="Close">
              <X className="h-4 w-4" />
            </Button>
          )}
        </div>

        {phase !== 'done' ? (
          <>
            <SegmentedControl
              className="mx-4 mt-4"
              value={mode}
              disabled={phase === 'importing'}
              onChange={resetForMode}
              options={[
                { value: 'json', label: 'mcpServers JSON' },
                { value: 'mcpb', label: 'MCPB bundle (.mcpb)' },
              ]}
            />

            {mode === 'json' ? (
              <>
                <Alert variant="info" className="mx-4 mt-3 px-3 py-2.5 text-xs">
                  <p className="font-semibold">
                    Standard MCP server format — used by Claude Desktop, Cursor, VS Code, Cline, and
                    others.
                  </p>
                  <ul className="ml-3 list-disc space-y-0.5 text-primary/80">
                    <li>
                      Paste the full JSON block. The top-level{' '}
                      <code className="font-mono">mcpServers</code> key is required.
                    </li>
                    <li>
                      Each object key becomes the connector{' '}
                      <span className="font-semibold">ID</span> and display name.
                    </li>
                    <li>
                      <code className="font-mono">command</code> +{' '}
                      <code className="font-mono">args</code> {'->'} local command transport,
                      normalized to a managed container runtime.
                    </li>
                    <li>
                      <code className="font-mono">url</code> {'->'} HTTP/SSE transport (remote MCP
                      server).
                    </li>
                    <li>
                      <code className="font-mono">env</code> key names are shown as a reminder — set
                      the actual secret values via container environment variables.
                    </li>
                    <li>
                      <strong>For HTTP/SSE remote servers only</strong>: if authentication is required,
                      create a credential with the key name exactly{' '}
                      <code className="font-mono">MCP_AUTH_TOKEN</code> (case-sensitive). Mismatch will
                      cause 401 Unauthorized errors.
                    </li>
                    <li>
                      Do not use <code className="font-mono">MCP_AUTH_TOKEN</code> for local/command
                      connectors or MCPB/containerized connectors unless the connector itself explicitly
                      expects that env key.
                    </li>
                    <li>
                      Command-based imports are normalized into managed container runtimes. Their
                      command and args are passed through as-is inside that runtime.
                    </li>
                    <li>
                      Connectors that already exist will be{' '}
                      <span className="font-semibold">updated</span>; new ones will be added.
                    </li>
                  </ul>
                </Alert>

                <div className="p-4 space-y-2">
                  <div className="flex items-center justify-between">
                    <label className="text-xs font-medium" htmlFor="connector-import-json">
                      Paste JSON
                    </label>
                    {!raw.trim() && (
                      <button
                        onClick={() => setRaw(IMPORT_EXAMPLE)}
                        className="text-xs text-muted-foreground hover:text-foreground underline underline-offset-2"
                      >
                        Insert example
                      </button>
                    )}
                  </div>
                  <textarea
                    id="connector-import-json"
                    aria-label="Paste JSON"
                    rows={10}
                    spellCheck={false}
                    className="w-full rounded border border-border bg-background px-3 py-2 text-xs font-mono"
                    placeholder={
                      '{\n  "mcpServers": {\n    "my-server": {\n      "command": "npx",\n      "args": ["-y", "@my/mcp-server"]\n    }\n  }\n}'
                    }
                    value={raw}
                    onChange={(e) => setRaw(e.target.value)}
                  />

                  {raw.trim() && parseError && (
                    <div className="flex items-start gap-1.5 text-xs text-destructive">
                      <AlertCircle className="h-3.5 w-3.5 shrink-0 mt-0.5" />
                      <span>{parseError}</span>
                    </div>
                  )}

                  {entries.length > 0 && (
                    <div className="space-y-2">
                      <div className="rounded-lg border border-border overflow-hidden">
                        <table className="w-full text-xs">
                          <thead className="bg-muted">
                            <tr>
                              <th className="px-3 py-1.5 text-left font-medium text-muted-foreground">
                                ID
                              </th>
                              <th className="px-3 py-1.5 text-left font-medium text-muted-foreground">
                                Transport
                              </th>
                              <th className="px-3 py-1.5 text-left font-medium text-muted-foreground">
                                Command / URL
                              </th>
                              <th className="px-3 py-1.5 text-left font-medium text-muted-foreground">
                                Args
                              </th>
                              <th className="px-3 py-1.5 text-left font-medium text-muted-foreground">
                                Env keys
                              </th>
                              <th className="px-3 py-1.5 text-left font-medium text-muted-foreground">
                                Action
                              </th>
                            </tr>
                          </thead>
                          <tbody>
                            {entries.map((e: McpImportEntry) => {
                              const isHttp = e.transport === 'http';
                              const argsPreview = e.args.length > 0 ? e.args.join(' ') : '—';
                              const argsTitle = e.args.length > 0 ? e.args.join('\n') : undefined;
                              return (
                                <tr key={e.id} className="border-t border-border">
                                  <td className="px-3 py-1.5 font-mono">{e.id}</td>
                                  <td
                                    className="px-3 py-1.5 text-muted-foreground"
                                    title={
                                      isHttp
                                        ? 'Remote HTTP/SSE MCP server'
                                        : 'Command-based MCP server'
                                    }
                                  >
                                    {isHttp ? 'HTTP' : 'Local command'}
                                  </td>
                                  <td
                                    className="px-3 py-1.5 font-mono text-muted-foreground max-w-40 truncate"
                                    title={isHttp ? e.url : e.command}
                                  >
                                    {isHttp ? e.url : e.command || '—'}
                                  </td>
                                  <td
                                    className="px-3 py-1.5 font-mono text-muted-foreground max-w-48 truncate"
                                    title={argsTitle}
                                  >
                                    {argsPreview}
                                  </td>
                                  <td className="px-3 py-1.5 text-muted-foreground">
                                    {e.envKeys.length > 0 ? (
                                      <span title={e.envKeys.join(', ')}>
                                        {e.envKeys.join(', ')}
                                      </span>
                                    ) : (
                                      <span className="opacity-30">—</span>
                                    )}
                                  </td>
                                  <td className="px-3 py-1.5">
                                    {existingIds.has(e.id) ? (
                                      <span className="text-attention">
                                        update
                                      </span>
                                    ) : (
                                      <span className="text-done">
                                        add
                                      </span>
                                    )}
                                  </td>
                                </tr>
                              );
                            })}
                          </tbody>
                        </table>
                      </div>
                    </div>
                  )}
                </div>
              </>
            ) : (
              <div className="p-4 space-y-3">
                <Alert variant="info" className="px-3 py-2.5 text-xs">
                  <p className="font-semibold">MCPB bundle import</p>
                  <ul className="ml-3 list-disc space-y-0.5 text-primary/80">
                    <li>Upload a single MCPB bundle file with the .mcpb extension.</li>
                    <li>Generic .zip archives are rejected by the UI and backend.</li>
                    <li>The import creates or updates exactly one connector from the bundle.</li>
                  </ul>
                </Alert>

                <label className="block rounded-lg border border-dashed border-border p-3">
                  <span className="mb-2 block text-xs font-medium">Choose MCPB file</span>
                  <input
                    type="file"
                    accept=".mcpb,application/zip"
                    onChange={(e) => {
                      const selected = e.target.files?.[0] ?? null;
                      setMcpbFile(selected);
                      setMcpbError(null);
                    }}
                    className="block w-full text-xs"
                  />
                </label>

                {mcpbFile && (
                  <div className="rounded-lg border border-border bg-muted/30 px-3 py-2 text-xs">
                    <p>
                      <span className="font-medium">Selected:</span>{' '}
                      <span className="font-mono">{mcpbFile.name}</span>
                    </p>
                    <p className="text-muted-foreground">Size: {formatFileSize(mcpbFile.size)}</p>
                  </div>
                )}

                {mcpbError && (
                  <div className="flex items-start gap-1.5 text-xs text-destructive">
                    <AlertCircle className="h-3.5 w-3.5 shrink-0 mt-0.5" />
                    <span>{mcpbError}</span>
                  </div>
                )}
              </div>
            )}

            <div className="flex justify-end gap-2 border-t border-border px-4 py-3">
              <Button type="button" variant="outline" size="sm" onClick={onClose}>
                Cancel
              </Button>
              <Button
                type="button"
                size="sm"
                disabled={
                  (mode === 'json' ? entries.length === 0 : !mcpbFile) || phase === 'importing'
                }
                onClick={() => void handleImport()}
              >
                {phase === 'importing' && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
                {phase === 'importing'
                  ? 'Importing…'
                  : mode === 'json'
                    ? `Import ${entries.length} connector${entries.length !== 1 ? 's' : ''}`
                    : 'Import MCPB bundle'}
              </Button>
            </div>
          </>
        ) : (
          <>
            {mode === 'json' ? (
              <div className="p-4 space-y-2 max-h-[50vh] overflow-y-auto">
                <p className="text-xs text-muted-foreground mb-3">
                  {results.filter((r) => r.success).length} of {results.length} connectors imported
                  successfully.
                </p>
                {results.map((r) => (
                  <div key={r.entry.id} className="flex items-start gap-2 text-sm">
                    {r.success ? (
                      <CheckCircle2 className="h-4 w-4 text-done shrink-0 mt-0.5" />
                    ) : (
                      <AlertCircle className="h-4 w-4 text-destructive shrink-0 mt-0.5" />
                    )}
                    <div>
                      <span className="font-mono text-xs">{r.entry.id}</span>
                      {r.entry.envKeys.length > 0 && r.success && (
                        <p className="text-xs text-muted-foreground">
                          Set values for:{' '}
                          <span className="font-mono">{r.entry.envKeys.join(', ')}</span>
                        </p>
                      )}
                      {!r.success && <p className="text-xs text-destructive">{r.error}</p>}
                    </div>
                  </div>
                ))}
              </div>
            ) : (
              <div className="p-4 space-y-3">
                {mcpbResult?.outcome === 'created' || mcpbResult?.outcome === 'updated' ? (
                  <Alert variant="success" className="flex items-start gap-2 px-3 py-2">
                    <CheckCircle2 className="h-4 w-4 shrink-0 mt-0.5" />
                    <div>
                      <p className="font-medium">{mcpbResult.message}</p>
                      {mcpbResult.connector && (
                        <p className="text-xs">
                          Imported connector:{' '}
                          <span className="font-mono">{mcpbResult.connector.id}</span> (
                          {mcpbResult.outcome})
                        </p>
                      )}
                    </div>
                  </Alert>
                ) : (
                  <Alert variant="destructive" className="flex items-start gap-2 px-3 py-2">
                    <AlertCircle className="h-4 w-4 shrink-0 mt-0.5" />
                    <div>
                      <p className="font-medium">MCPB import failed</p>
                      <p className="text-xs">{mcpbResult?.message ?? 'No response received.'}</p>
                    </div>
                  </Alert>
                )}

                {mcpbResult && (
                  <div className="rounded-lg border border-border overflow-hidden">
                    <table className="w-full text-xs">
                      <tbody>
                        <tr className="border-t border-border first:border-0">
                          <td className="px-3 py-2 text-muted-foreground w-40">Outcome</td>
                          <td className="px-3 py-2 font-medium">{mcpbResult.outcome}</td>
                        </tr>
                        <tr className="border-t border-border">
                          <td className="px-3 py-2 text-muted-foreground">Server ID</td>
                          <td className="px-3 py-2 font-mono">{mcpbResult.serverId || '—'}</td>
                        </tr>
                        <tr className="border-t border-border">
                          <td className="px-3 py-2 text-muted-foreground">Manifest version</td>
                          <td className="px-3 py-2">{mcpbResult.manifestVersion || '—'}</td>
                        </tr>
                        <tr className="border-t border-border">
                          <td className="px-3 py-2 text-muted-foreground">Server type</td>
                          <td className="px-3 py-2">{mcpbResult.serverType || '—'}</td>
                        </tr>
                      </tbody>
                    </table>
                  </div>
                )}
              </div>
            )}
            <div className="flex justify-end border-t border-border px-4 py-3">
              <Button type="button" size="sm" onClick={onClose}>
                Done
              </Button>
            </div>
          </>
        )}
      </DialogContent>
    </Dialog>
  );
};

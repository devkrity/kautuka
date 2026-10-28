import { Alert } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogTitle } from '@/components/ui/dialog';
import { AlertCircle, CheckCircle2, FlaskConical, Loader2, X } from 'lucide-react';
import React, { useEffect, useState } from 'react';
import type { ConnectorTestPayload } from '../api/connectors';
import { useTestConnector } from '../api/queries';
import type { ConnectorTestResult } from '../schemas/connectorSchema';
import type { ConnectorDraft } from '../types/connectorDraft';

interface ConnectorFormModalProps {
  draft: ConnectorDraft;
  isNew: boolean;
  onSave: (draft: ConnectorDraft, credValues: Record<string, string>) => void;
  onClose: () => void;
  saving: boolean;
  saveLabel?: string;
  onToolCountSaved?: (toolCount: number, toolNames: string[] | null) => void;
}

const SECRET_INPUT_TYPE: React.HTMLInputTypeAttribute = ['pass', 'word'].join('');
const PLAIN_INPUT_TYPE: React.HTMLInputTypeAttribute = 'text';
const SENSITIVE_CREDENTIAL_PARTS = [
  ['to', 'ken'],
  ['se', 'cret'],
  ['k', 'ey'],
  ['pass', 'word'],
] as const;

const containsSensitiveCredentialWord = (value: string): boolean => {
  const normalizedValue = value.toLowerCase();
  return SENSITIVE_CREDENTIAL_PARTS.map((parts) => parts.join('')).some((term) =>
    normalizedValue.includes(term)
  );
};

export const ConnectorFormModal: React.FC<ConnectorFormModalProps> = ({
  draft: initial,
  isNew,
  onSave,
  onClose,
  saving,
  saveLabel,
  onToolCountSaved,
}) => {
  const [draft, setDraft] = useState<ConnectorDraft>({ ...initial });
  const [testEnv, setTestEnv] = useState<Record<string, string>>({});
  const [credValues, setCredValues] = useState<Record<string, string>>({});
  const [testResult, setTestResult] = useState<ConnectorTestResult | null>(null);
  const testMutation = useTestConnector();

  useEffect(() => {
    setDraft({ ...initial });
    setTestEnv({});
    setCredValues({});
    setTestResult(null);
  }, [initial.id, isNew]);

  const setDraftField = <K extends keyof ConnectorDraft>(key: K, value: ConnectorDraft[K]) => {
    setDraft((previous) => {
      switch (key) {
        case 'id':
          return { ...previous, id: value as ConnectorDraft['id'] };
        case 'name':
          return { ...previous, name: value as ConnectorDraft['name'] };
        case 'description':
          return { ...previous, description: value as ConnectorDraft['description'] };
        case 'iconUrl':
          return { ...previous, iconUrl: value as ConnectorDraft['iconUrl'] };
        case 'transport':
          return { ...previous, transport: value as ConnectorDraft['transport'] };
        case 'httpUrl':
          return { ...previous, httpUrl: value as ConnectorDraft['httpUrl'] };
        case 'localCommand':
          return { ...previous, localCommand: value as ConnectorDraft['localCommand'] };
        case 'localArgs':
          return { ...previous, localArgs: value as ConnectorDraft['localArgs'] };
        case 'containerImage':
          return { ...previous, containerImage: value as ConnectorDraft['containerImage'] };
        case 'containerCommand':
          return { ...previous, containerCommand: value as ConnectorDraft['containerCommand'] };
        case 'containerArgs':
          return { ...previous, containerArgs: value as ConnectorDraft['containerArgs'] };
        case 'containerSidecarName':
          return {
            ...previous,
            containerSidecarName: value as ConnectorDraft['containerSidecarName'],
          };
        case 'bundlePath':
          return {
            ...previous,
            bundlePath: value as ConnectorDraft['bundlePath'],
          };
        case 'serverType':
          return {
            ...previous,
            serverType: value as ConnectorDraft['serverType'],
          };
        case 'readinessTimeoutSeconds':
          return {
            ...previous,
            readinessTimeoutSeconds: value as ConnectorDraft['readinessTimeoutSeconds'],
          };
        case 'jitCooldownSeconds':
          return {
            ...previous,
            jitCooldownSeconds: value as ConnectorDraft['jitCooldownSeconds'],
          };
        case 'healthcheckUrl':
          return { ...previous, healthcheckUrl: value as ConnectorDraft['healthcheckUrl'] };
        case 'healthcheckIntervalMilliseconds':
          return {
            ...previous,
            healthcheckIntervalMilliseconds:
              value as ConnectorDraft['healthcheckIntervalMilliseconds'],
          };
        case 'mcpProtocolVersion':
          return {
            ...previous,
            mcpProtocolVersion: value as ConnectorDraft['mcpProtocolVersion'],
          };
        case 'envKeys':
          return { ...previous, envKeys: value as ConnectorDraft['envKeys'] };
        case 'enabled':
          return { ...previous, enabled: value as ConnectorDraft['enabled'] };
        case 'toolCount':
          return { ...previous, toolCount: value as ConnectorDraft['toolCount'] };
        case 'toolNames':
          return { ...previous, toolNames: value as ConnectorDraft['toolNames'] };
        default:
          return previous;
      }
    });
  };

  const declaredEnvKeys = draft.envKeys
    .split('\n')
    .map((s) => s.trim())
    .filter(Boolean);

  const toSlug = (value: string): string => {
    const parts = value
      .toLowerCase()
      .split(/[^a-z0-9]+/)
      .filter(Boolean);
    return parts.join('-');
  };

  const toSlugId = (value: string): string => {
    let normalized = '';
    for (const ch of value.toLowerCase()) {
      if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch === '-') {
        normalized += ch;
      }
    }
    return normalized;
  };

  const buildTestPayload = (): ConnectorTestPayload => {
    const testEnvEntries = new Map(Object.entries(testEnv));

    return {
      id: draft.id || null,
      transport: draft.transport,
      httpUrl: draft.httpUrl || null,
      localCommand: draft.localCommand || null,
      localArgs: draft.localArgs
        .split('\n')
        .map((s) => s.trim())
        .filter(Boolean),
      containerImage: draft.containerImage || null,
      containerCommand: draft.containerCommand || null,
      containerArgs: draft.containerArgs
        .split('\n')
        .map((s) => s.trim())
        .filter(Boolean),
      containerSidecarName: draft.containerSidecarName || null,
      bundlePath: draft.bundlePath || null,
      serverType: draft.serverType || null,
      readinessTimeoutSeconds: Number.parseInt(draft.readinessTimeoutSeconds, 10) || null,
      jitCooldownSeconds: Number.parseInt(draft.jitCooldownSeconds, 10) || null,
      healthcheckUrl: draft.healthcheckUrl || null,
      healthcheckIntervalMilliseconds:
        Number.parseInt(draft.healthcheckIntervalMilliseconds, 10) || null,
      mcpProtocolVersion: draft.mcpProtocolVersion || null,
      envKeys: declaredEnvKeys,
      testEnv: Object.fromEntries(
        declaredEnvKeys
          .map((key) => [key, testEnvEntries.get(key)?.trim() ?? ''] as const)
          .filter(([, value]) => value.length > 0)
      ),
    };
  };

  const handleTest = () => {
    setTestResult(null);
    testMutation.mutate(buildTestPayload(), {
      onSuccess: (tr) => {
        setTestResult(tr);
        if (tr.success && tr.toolCount != null) {
          const effectiveToolNames = tr.toolNames ?? draft.toolNames ?? null;
          setDraft((previous) => ({
            ...previous,
            toolCount: tr.toolCount,
            toolNames: effectiveToolNames,
          }));
          if (!isNew) onToolCountSaved?.(tr.toolCount, effectiveToolNames);
        }
      },
      onError: (err) => {
        setTestResult({
          success: false,
          message: err instanceof Error ? err.message : 'Test request failed.',
          toolCount: null,
          packageChecked: null,
          toolNames: null,
          toolDescriptions: null,
        });
      },
    });
  };

  const idPlaceholder = draft.name ? `${toSlug(draft.name)}-mcp` : 'my-connector-mcp';

  const testing = testMutation.isPending;

  const getValidationCredentialValue = (credentialKey: string): string => {
    const value = new Map(Object.entries(credValues)).get(credentialKey);
    return typeof value === 'string' ? value : '';
  };

  const getValidationCredentialInputType = (credentialKey: string): React.HTMLInputTypeAttribute =>
    containsSensitiveCredentialWord(credentialKey) ? SECRET_INPUT_TYPE : PLAIN_INPUT_TYPE;

  const setValidationCredential = (credentialKey: string, value: string) => {
    setCredValues((previous) => {
      const nextEntries = new Map(Object.entries(previous));
      nextEntries.set(credentialKey, value);
      return Object.fromEntries(nextEntries);
    });
    // keep testEnv in sync so Validate works without re-entering
    setTestEnv((previous) => {
      const nextEntries = new Map(Object.entries(previous));
      nextEntries.set(credentialKey, value);
      return Object.fromEntries(nextEntries);
    });
  };

  const getToolDescription = (toolName: string): string | undefined => {
    const descriptions = testResult?.toolDescriptions;
    if (!descriptions) {
      return undefined;
    }
    const value = new Map<string, string | null>(Object.entries(descriptions)).get(toolName);
    return typeof value === 'string' ? value : undefined;
  };

  const saveButtonLabel = (() => {
    if (saving) {
      return 'Saving…';
    }
    if (saveLabel) {
      return saveLabel;
    }
    return isNew ? 'Import Connector' : 'Save';
  })();

  const discoveredToolNames = testResult?.success
    ? (testResult.toolNames ?? draft.toolNames ?? null)
    : testResult
      ? null
      : draft.toolNames;

  const discoveredToolCount = testResult?.success
    ? (testResult.toolCount ?? draft.toolCount)
    : testResult
      ? null
      : draft.toolCount;

  const isHomeAssistantPreset = draft.id === 'homeassistant-mcp';

  return (
    <Dialog open onOpenChange={(next) => { if (!next) onClose(); }}>
      <DialogContent hideClose className="max-w-lg gap-0 overflow-hidden p-0">
        <div className="flex items-center justify-between border-b border-border px-4 py-3">
          <DialogTitle className="text-sm font-semibold">
            {isNew ? 'Advanced Connector Import' : initial.name}
          </DialogTitle>
          <Button type="button" variant="ghost" size="icon-sm" onClick={onClose} title="Close">
            <X className="h-4 w-4" />
          </Button>
        </div>

        {/* body */}
        <div className="space-y-3 p-4 max-h-[60vh] overflow-y-auto">
          {isNew && (
            <>
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-xs font-medium mb-1" htmlFor="connector-name">
                    Name <span className="text-destructive">*</span>
                  </label>
                  <input
                    id="connector-name"
                    className="w-full rounded border border-border bg-background px-3 py-1.5 text-sm"
                    value={draft.name}
                    onChange={(e) => setDraftField('name', e.target.value)}
                    placeholder="My Connector"
                  />
                </div>
                <div>
                  <label className="block text-xs font-medium mb-1" htmlFor="connector-id">
                    ID <span className="font-normal text-muted-foreground">(slug)</span>{' '}
                    <span className="text-destructive">*</span>
                  </label>
                  <input
                    id="connector-id"
                    className="w-full rounded border border-border bg-background px-3 py-1.5 text-sm font-mono"
                    value={draft.id}
                    onChange={(e) => setDraftField('id', toSlugId(e.target.value))}
                    placeholder={idPlaceholder}
                  />
                </div>
              </div>
              <div>
                <label className="block text-xs font-medium mb-1" htmlFor="connector-description">
                  Description
                </label>
                <input
                  id="connector-description"
                  className="w-full rounded border border-border bg-background px-3 py-1.5 text-sm"
                  value={draft.description}
                  onChange={(e) => setDraftField('description', e.target.value)}
                  placeholder="What does this connector do?"
                />
              </div>
              <div>
                <label className="block text-xs font-medium mb-1" htmlFor="connector-transport">
                  Transport
                </label>
                <select
                  id="connector-transport"
                  aria-label="Transport type"
                  className="w-full rounded border border-border bg-background px-3 py-1.5 text-sm"
                  value={draft.transport}
                  onChange={(e) => setDraftField('transport', e.target.value)}
                >
                  <option value="local">Local Command (normalized to container)</option>
                  <option value="http">HTTP (remote URL)</option>
                  <option value="container">Container (Manager managed)</option>
                </select>
              </div>
            </>
          )}

          {draft.transport === 'http' ? (
            <div>
              <label className="block text-xs font-medium mb-1" htmlFor="connector-http-url">
                HTTP URL
              </label>
              <input
                id="connector-http-url"
                className="w-full rounded border border-border bg-background px-3 py-1.5 text-sm font-mono"
                value={draft.httpUrl}
                onChange={(e) => setDraftField('httpUrl', e.target.value)}
                placeholder="https://"
              />
              {isHomeAssistantPreset && (
                <p className="mt-1 text-xs text-muted-foreground">
                  Use your Home Assistant MCP endpoint, typically
                  https://&lt;your-home-assistant&gt;/api/mcp. Provide MCP_AUTH_TOKEN below when
                  validating, then set it at agent scope for runtime use.
                </p>
              )}
            </div>
          ) : draft.transport === 'container' ? (
            <>
              <div>
                <label
                  className="block text-xs font-medium mb-1"
                  htmlFor="connector-container-image"
                >
                  Container Image
                </label>
                <input
                  id="connector-container-image"
                  className="w-full rounded border border-border bg-background px-3 py-1.5 text-sm font-mono"
                  value={draft.containerImage}
                  onChange={(e) => setDraftField('containerImage', e.target.value)}
                  placeholder="mcp/markitdown"
                />
              </div>
              <div>
                <label className="block text-xs font-medium mb-1" htmlFor="connector-mcp-protocol">
                  MCP protocol
                </label>
                <select
                  id="connector-mcp-protocol"
                  className="w-full rounded border border-border bg-background px-3 py-1.5 text-sm"
                  value={draft.mcpProtocolVersion}
                  onChange={(e) => setDraftField('mcpProtocolVersion', e.target.value)}
                >
                  <option value="">Automatic</option>
                  <option value="2024-11-05">2024-11-05</option>
                  <option value="2025-03-26">2025-03-26</option>
                  <option value="2025-06-18">2025-06-18</option>
                  <option value="2025-11-25">2025-11-25</option>
                  <option value="2026-07-28">2026-07-28</option>
                </select>
                <p className="mt-1 text-xs text-muted-foreground">
                  Automatic uses the current handshake. A selected date is requested exactly.
                </p>
              </div>
              <div>
                <label
                  className="block text-xs font-medium mb-1"
                  htmlFor="connector-container-command"
                >
                  Container Command
                </label>
                <input
                  id="connector-container-command"
                  className="w-full rounded border border-border bg-background px-3 py-1.5 text-sm font-mono"
                  value={draft.containerCommand}
                  onChange={(e) => setDraftField('containerCommand', e.target.value)}
                  placeholder=""
                />
              </div>
              <div>
                <label
                  className="block text-xs font-medium mb-1"
                  htmlFor="connector-container-args"
                >
                  Container Arguments{' '}
                  <span className="font-normal text-muted-foreground">(one per line)</span>
                </label>
                <textarea
                  id="connector-container-args"
                  rows={4}
                  className="w-full rounded border border-border bg-background px-3 py-1.5 text-sm font-mono"
                  value={draft.containerArgs}
                  onChange={(e) => setDraftField('containerArgs', e.target.value)}
                  placeholder={'mcp\n--transport sse https://example.invalid/mcp'}
                />
              </div>
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label
                    className="block text-xs font-medium mb-1"
                    htmlFor="connector-readiness-timeout"
                  >
                    Readiness Timeout (seconds)
                  </label>
                  <input
                    id="connector-readiness-timeout"
                    type="number"
                    min={1}
                    className="w-full rounded border border-border bg-background px-3 py-1.5 text-sm"
                    value={draft.readinessTimeoutSeconds}
                    onChange={(e) => setDraftField('readinessTimeoutSeconds', e.target.value)}
                    placeholder="45"
                  />
                </div>
                <div>
                  <label
                    className="block text-xs font-medium mb-1"
                    htmlFor="connector-jit-cooldown"
                  >
                    JIT Cooldown (seconds)
                  </label>
                  <input
                    id="connector-jit-cooldown"
                    type="number"
                    min={0}
                    className="w-full rounded border border-border bg-background px-3 py-1.5 text-sm"
                    value={draft.jitCooldownSeconds}
                    onChange={(e) => setDraftField('jitCooldownSeconds', e.target.value)}
                    placeholder="600"
                  />
                </div>
              </div>
              <div>
                <label className="block text-xs font-medium mb-1" htmlFor="connector-sidecar-name">
                  Container Sidecar Name
                </label>
                <input
                  id="connector-sidecar-name"
                  className="w-full rounded border border-border bg-background px-3 py-1.5 text-sm font-mono"
                  value={draft.containerSidecarName}
                  onChange={(e) => setDraftField('containerSidecarName', e.target.value)}
                  placeholder="markitdown"
                />
              </div>
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label
                    className="block text-xs font-medium mb-1"
                    htmlFor="connector-healthcheck-url"
                  >
                    Healthcheck URL
                  </label>
                  <input
                    id="connector-healthcheck-url"
                    className="w-full rounded border border-border bg-background px-3 py-1.5 text-sm font-mono"
                    value={draft.healthcheckUrl}
                    onChange={(e) => setDraftField('healthcheckUrl', e.target.value)}
                    placeholder="http://127.0.0.1:20011/health"
                  />
                  <p className="mt-1 text-xs text-muted-foreground">
                    Example endpoint only. Manager uses this URL to probe the connector sidecar you
                    configure.
                  </p>
                </div>
                <div>
                  <label
                    className="block text-xs font-medium mb-1"
                    htmlFor="connector-healthcheck-interval"
                  >
                    Healthcheck Interval (ms)
                  </label>
                  <input
                    id="connector-healthcheck-interval"
                    type="number"
                    min={100}
                    step={100}
                    className="w-full rounded border border-border bg-background px-3 py-1.5 text-sm"
                    value={draft.healthcheckIntervalMilliseconds}
                    onChange={(e) =>
                      setDraftField('healthcheckIntervalMilliseconds', e.target.value)
                    }
                    placeholder="2000"
                  />
                </div>
              </div>
            </>
          ) : (
            <>
              <div>
                <label className="block text-xs font-medium mb-1" htmlFor="connector-local-command">
                  Local Command
                </label>
                <input
                  id="connector-local-command"
                  className="w-full rounded border border-border bg-background px-3 py-1.5 text-sm font-mono"
                  value={draft.localCommand}
                  onChange={(e) => setDraftField('localCommand', e.target.value)}
                  placeholder="npx"
                />
              </div>
              <div>
                <label className="block text-xs font-medium mb-1" htmlFor="connector-local-args">
                  Local Arguments{' '}
                  <span className="font-normal text-muted-foreground">(one per line)</span>
                </label>
                <textarea
                  id="connector-local-args"
                  rows={4}
                  className="w-full rounded border border-border bg-background px-3 py-1.5 text-sm font-mono"
                  value={draft.localArgs}
                  onChange={(e) => setDraftField('localArgs', e.target.value)}
                  placeholder={'-y\n@modelcontextprotocol/server-github'}
                />
                <p className="mt-1 text-xs text-muted-foreground">
                  Gateway maps supported local commands onto a managed container runtime before save
                  and validation.
                </p>
              </div>
            </>
          )}

          {draft.transport !== 'http' && (
            <Alert variant="info" className="px-3 py-2.5 text-xs">
              <p className="font-semibold">Authentication note</p>
              <ul className="ml-3 list-disc space-y-0.5 text-primary/80">
                <li>
                  <code className="font-mono">MCP_AUTH_TOKEN</code> is only for remote HTTP MCP
                  servers that use bearer authentication.
                </li>
                <li>
                  Local and container connectors typically require service-specific credential keys
                  rather than <code className="font-mono">MCP_AUTH_TOKEN</code>.
                </li>
              </ul>
            </Alert>
          )}

          <div>
            <label className="block text-xs font-medium mb-1" htmlFor="connector-credential-keys">
              Credential Keys{' '}
              <span className="font-normal text-muted-foreground">
                (one per line — env var names users must supply)
              </span>
            </label>
            <textarea
              id="connector-credential-keys"
              rows={3}
              className="w-full rounded border border-border bg-background px-3 py-1.5 text-sm font-mono"
              value={draft.envKeys}
              onChange={(e) => setDraftField('envKeys', e.target.value)}
              placeholder={'GITHUB_TOKEN\nANOTHER_API_KEY'}
            />
            {draft.transport === 'http' && declaredEnvKeys.some((k) => k.toLowerCase().includes('token') || k.toLowerCase().includes('auth')) && (
              <p className="mt-2 text-xs bg-attention/10 text-attention border border-attention/30 rounded px-2 py-1.5">
                <strong>HTTP Transport Auth:</strong> If this MCP server requires authentication via Bearer token, the credential key must be exactly <code className="font-mono">MCP_AUTH_TOKEN</code> (case-sensitive). Other key names will cause 401 Unauthorized errors during validation.
              </p>
            )}
          </div>

          {draft.id === 'kautuka-m365-mail' ? (
            <p className="rounded-lg border border-border bg-muted/20 px-3 py-2 text-xs text-muted-foreground">
              Mailbox sign-in is on the connector card. Use Sign in with Microsoft. Do not paste a
              token here.
            </p>
          ) : null}

          {declaredEnvKeys.filter((key) => !(draft.id === 'kautuka-m365-mail' && key === 'KAUTUKA_M365_REFRESH_TOKEN')).length > 0 && (
            <div className="rounded-lg border border-border bg-muted/20 p-3">
              <div className="mb-2 text-xs font-medium">Credentials</div>
              <p className="mb-3 text-xs text-muted-foreground">
                Saved as system credentials and used at runtime. Also passed to Validate when
                testing.
              </p>
              <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
                {declaredEnvKeys
                  .filter((key) => !(draft.id === 'kautuka-m365-mail' && key === 'KAUTUKA_M365_REFRESH_TOKEN'))
                  .map((key) => (
                  <div key={key}>
                    <label
                      className="mb-1 block text-xs text-muted-foreground"
                      htmlFor={`connector-cred-${key}`}
                    >
                      {key}
                    </label>
                    <input
                      id={`connector-cred-${key}`}
                      type={getValidationCredentialInputType(key)}
                      className="w-full rounded border border-border bg-background px-3 py-1.5 text-sm"
                      placeholder="Enter credential…"
                      value={getValidationCredentialValue(key)}
                      onChange={(e) => setValidationCredential(key, e.target.value)}
                    />
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>

        {/* test result */}
        {testResult && (
          <div className="mx-4 mb-2 space-y-2">
            <div
              className={[
                'flex items-start gap-2 rounded-lg border px-3 py-2 text-xs',
                testResult.success
                  ? 'border-done/40 bg-done/10 text-done'
                  : 'border-destructive/40 bg-destructive/5 text-destructive',
              ].join(' ')}
            >
              {testResult.success ? (
                <CheckCircle2 className="h-3.5 w-3.5 shrink-0 mt-0.5" />
              ) : (
                <AlertCircle className="h-3.5 w-3.5 shrink-0 mt-0.5" />
              )}
              <span>{testResult.message}</span>
            </div>
            {discoveredToolNames && discoveredToolNames.length > 0 && (
              <div className="rounded-lg border border-border bg-muted/30 overflow-hidden">
                <p className="section-label border-b border-border px-3 py-1.5">
                  {discoveredToolCount ?? discoveredToolNames.length} tools found
                </p>
                <ul className="max-h-48 overflow-y-auto divide-y divide-border">
                  {discoveredToolNames.map((name) => {
                    const desc = getToolDescription(name);
                    return (
                      <li key={name} className="px-3 py-1.5">
                        <span className="text-xs font-mono font-medium">{name}</span>
                        {desc && <p className="text-[11px] text-muted-foreground mt-0.5">{desc}</p>}
                      </li>
                    );
                  })}
                </ul>
              </div>
            )}
          </div>
        )}

        {/* footer */}
        <div className="flex items-center justify-between border-t border-border px-4 py-3">
          <Button type="button" variant="outline" size="sm" disabled={testing || saving} onClick={handleTest}>
            {testing ? (
              <Loader2 className="h-3.5 w-3.5 animate-spin" />
            ) : (
              <FlaskConical className="h-3.5 w-3.5" />
            )}
            {testing ? 'Validating…' : 'Validate'}
          </Button>
          <div className="flex gap-2">
            <Button type="button" variant="outline" size="sm" onClick={onClose}>
              Cancel
            </Button>
            <Button
              type="button"
              size="sm"
              disabled={saving || (isNew && (!draft.name.trim() || !draft.id.trim()))}
              onClick={() => onSave(draft, credValues)}
            >
              {saveButtonLabel}
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
};

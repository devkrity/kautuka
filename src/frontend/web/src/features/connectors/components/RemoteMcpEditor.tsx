import { useRef, useState } from 'react';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogTitle } from '@/components/ui/dialog';
import type { Connector, ConnectorTestResult, RemoteMcpHeader } from '../schemas/connectorSchema';
import { blankDraft, entryToDraft, type ConnectorDraft } from '../types/connectorDraft';
import { parseRemoteMcpUrl, suggestRemoteMcpIdentity } from '../types/remoteMcpConfiguration';
import type { RemoteCredentialPresence } from '../api/connectors';

export type RemoteCredentialMutation =
  { key: string; action: 'replace'; value: string } | { key: string; action: 'remove' };
type Props = {
  connector?: Connector;
  initialDraft?: ConnectorDraft;
  initialCredentials?: Record<string, string>;
  configuredKeys?: string[];
  credentialPresence?: RemoteCredentialPresence[];
  canManage?: boolean;
  canManageSystemCredentials?: boolean;
  onSave: (
    draft: ConnectorDraft,
    changes: RemoteCredentialMutation[],
    scope?: 'user' | 'system'
  ) => Promise<void>;
  onTest: (
    draft: ConnectorDraft,
    changes: RemoteCredentialMutation[],
    scope?: 'user' | 'system'
  ) => Promise<ConnectorTestResult>;
  onClose: () => void;
  onDraftChanged?: () => void;
  credentialStatusWarning?: string;
};

function requestFailureMessage(error: unknown): string | null {
  if (error && typeof error === 'object' && 'response' in error) {
    const response = (error as { response?: { data?: unknown } }).response;
    const data = response?.data;
    if (data && typeof data === 'object') {
      const record = data as { detail?: unknown; message?: unknown };
      const detail = typeof record.detail === 'string' ? record.detail.trim() : '';
      const message = typeof record.message === 'string' ? record.message.trim() : '';
      if (detail) return detail;
      if (message) return message;
    }
  }
  return error instanceof Error && error.message.trim() ? error.message : null;
}
const inputClass = 'w-full rounded border border-border bg-background px-3 py-2 text-sm';

export function RemoteMcpEditor({
  connector,
  initialDraft,
  initialCredentials = {},
  configuredKeys = [],
  credentialPresence = [],
  canManage = true,
  canManageSystemCredentials = false,
  onSave,
  onTest,
  onClose,
  onDraftChanged,
  credentialStatusWarning,
}: Props) {
  const [draft, setDraft] = useState<ConnectorDraft>(
    () =>
      initialDraft ?? (connector ? entryToDraft(connector) : { ...blankDraft(), transport: 'http' })
  );
  const [changes, setChanges] = useState<RemoteCredentialMutation[]>(() =>
    Object.entries(initialCredentials).map(([key, value]) => ({ key, action: 'replace', value }))
  );
  const [busy, setBusy] = useState<'save' | 'test' | null>(null);
  const [error, setError] = useState('');
  const [result, setResult] = useState<ConnectorTestResult | null>(null);
  const [credentialScope, setCredentialScope] = useState<'user' | 'system'>('user');
  const [sharedUrlChangeAcknowledged, setSharedUrlChangeAcknowledged] = useState(false);
  const [legacyAuthAcknowledged, setLegacyAuthAcknowledged] = useState(false);
  const initialEndpoint = useRef(draft.httpUrl);
  const canonicalEndpoint = (value: string) => {
    try {
      return new URL(value.trim()).href;
    } catch {
      return value.trim();
    }
  };
  const destinationChanged =
    canonicalEndpoint(draft.httpUrl) !== canonicalEndpoint(initialEndpoint.current);
  const personalDestinationChanged = !canManage && destinationChanged;
  const sharedDestinationChanged = !!connector && connector.scope === 'system' && canManage && destinationChanged;
  const credentials = draft.credentials ?? [];
  const headers = draft.httpHeaders ?? [];
  const authentication = headers.find(
    (header) =>
      header.credentialKey &&
      ((header.name.toLowerCase() === 'authorization' && header.prefix === 'Bearer ') ||
        !header.prefix)
  );
  const authKind = authentication
    ? authentication.name.toLowerCase() === 'authorization' && authentication.prefix === 'Bearer '
      ? 'bearer'
      : 'api-key'
    : 'none';
  const update = (next: Partial<ConnectorDraft>) => {
    if ('httpUrl' in next) setSharedUrlChangeAcknowledged(false);
    if ('credentials' in next || 'httpHeaders' in next) setLegacyAuthAcknowledged(false);
    onDraftChanged?.();
    setDraft((previous) => ({ ...previous, ...next }));
    setResult(null);
    setError('');
  };
  const changeCredential = (change: RemoteCredentialMutation) => {
    onDraftChanged?.();
    setChanges((previous) => [...previous.filter((item) => item.key !== change.key), change]);
    setResult(null);
    setError('');
  };
  const setAuthentication = (kind: string) => {
    const remaining = headers.filter((header) => header !== authentication);
    if (kind === 'none') {
      update({
        httpHeaders: remaining,
        credentials: credentials.filter(
          (credential) =>
            credential.key !== authentication?.credentialKey ||
            remaining.some((header) => header.credentialKey === credential.key)
        ),
      });
      return;
    }
    const key = authentication?.credentialKey ?? 'api-key';
    update({
      credentials: credentials.some((item) => item.key === key)
        ? credentials
        : [...credentials, { key, label: 'API key', required: true }],
      httpHeaders: [
        ...remaining,
        {
          name: kind === 'bearer' ? 'Authorization' : 'x-api-key',
          credentialKey: key,
          ...(kind === 'bearer' ? { prefix: 'Bearer ' } : {}),
        },
      ],
    });
  };
  const updateHeader = (index: number, header: RemoteMcpHeader) =>
    update({ httpHeaders: headers.map((item, position) => (position === index ? header : item)) });
  const mutations = () =>
    changes.filter(
      (change) =>
        credentials.some((credential) => credential.key === change.key) &&
        (change.action === 'remove' || change.value.trim().length > 0)
    );
  const validate = (): ConnectorDraft => {
    const url = parseRemoteMcpUrl(draft.httpUrl);
    if (!draft.name.trim()) throw new Error('Enter a connector name.');
    const identity = suggestRemoteMcpIdentity(url.href);
    return {
      ...draft,
      id: draft.id || identity.id,
      httpUrl: url.href,
      transport: 'http',
      envKeys: '',
    };
  };
  const run = async (kind: 'save' | 'test') => {
    setError('');
    setBusy(kind);
    let candidate: ConnectorDraft;
    try {
      candidate = validate();
      if (connector?.requiresCredentialReentry && !legacyAuthAcknowledged)
        throw new Error('Confirm the new authentication choice before testing or saving.');
      if (connector?.requiresCredentialReentry) candidate.confirmLegacyAuthReplacement = true;
      if (kind === 'save' && sharedDestinationChanged && !sharedUrlChangeAcknowledged)
        throw new Error('Confirm that everyone will need to enter credentials again before saving this address change.');
      if (kind === 'save' && personalDestinationChanged) {
        const referenced = new Set(
          headers.flatMap((header) => (header.credentialKey ? [header.credentialKey] : []))
        );
        if ([...referenced].some((key) => !mutations().some((change) => change.key === key)))
          throw new Error(
            'For a new personal server URL, replace or explicitly omit each referenced credential before saving.'
          );
      }
    } catch (validation) {
      setError(
        validation instanceof Error ? validation.message : 'Check the connector configuration.'
      );
      setBusy(null);
      return;
    }
    try {
      if (kind === 'test') setResult(await onTest(candidate, mutations(), credentialScope));
      else await onSave(candidate, mutations(), credentialScope);
    } catch (failure) {
      setError(
        kind === 'save'
          ? 'Could not save this connector. Your entries are retained; correct any configuration errors and retry.'
          : (requestFailureMessage(failure) ??
            'Connection test failed. Check the endpoint and credentials, then retry.')
      );
    } finally {
      setBusy(null);
    }
  };

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open && !busy) onClose();
      }}
    >
      <DialogContent
        className="max-h-[90vh] max-w-2xl overflow-y-auto"
        onEscapeKeyDown={(event) => {
          if (busy) event.preventDefault();
        }}
        onPointerDownOutside={(event) => {
          if (busy) event.preventDefault();
        }}
      >
        <DialogTitle>{connector ? 'Configure remote MCP' : 'Add remote MCP'}</DialogTitle>
        <div className="space-y-4">
          {credentialStatusWarning && (
            <p role="alert" className="text-sm text-destructive">
              {credentialStatusWarning}
            </p>
          )}
          <label className="block text-sm">
            Name
            <input
              className={inputClass}
              value={draft.name}
              disabled={!canManage || !!busy}
              onChange={(event) => update({ name: event.target.value })}
            />
          </label>
          <label className="block text-sm">
            Server URL
            <input
              className={inputClass}
              type="url"
              value={draft.httpUrl}
              disabled={!!busy}
              placeholder="https://example.com/mcp"
              onChange={(event) => {
                const httpUrl = event.target.value;
                let suggestion: Partial<ConnectorDraft> = {};
                if (!connector && !draft.name) {
                  try {
                    suggestion = suggestRemoteMcpIdentity(httpUrl);
                  } catch {
                    /* An incomplete URL is normal while typing. */
                  }
                }
                update({ ...suggestion, httpUrl });
              }}
            />
          </label>
          {destinationChanged && connector && (
            <p className="text-sm text-muted-foreground">
              Credentials are bound to server URLs. Keys for a different URL will not be forwarded.
              {personalDestinationChanged
                ? ' Replace or explicitly omit each credential before saving your new personal URL.'
                : ''}
            </p>
          )}
          {sharedDestinationChanged && (
            <div className="space-y-2 rounded border border-amber-500/50 bg-amber-500/10 p-3 text-sm">
              <p role="alert">Changing this shared address means everyone who uses it will need to enter their key again.</p>
              <label className="flex items-start gap-2">
                <input type="checkbox" checked={sharedUrlChangeAcknowledged} disabled={!!busy}
                  onChange={(event) => setSharedUrlChangeAcknowledged(event.target.checked)} />
                <span>I understand that users will need to enter their keys again.</span>
              </label>
            </div>
          )}
          {connector?.requiresCredentialReentry && canManage && (
            <label className="flex items-start gap-2 rounded border border-amber-500/50 bg-amber-500/10 p-3 text-sm">
              <input type="checkbox" checked={legacyAuthAcknowledged} disabled={!!busy} onChange={(event) => {
                setLegacyAuthAcknowledged(event.target.checked);
                onDraftChanged?.();
                setResult(null);
              }} />
              <span>I reviewed the new authentication choice. If I choose no key or an optional key, this connector may connect without one.</span>
            </label>
          )}
          <label className="block text-sm">
            Authentication
            <select
              className={inputClass}
              value={authKind}
              disabled={!canManage || !!busy}
              onChange={(event) => setAuthentication(event.target.value)}
            >
              <option value="none">None</option>
              <option value="bearer">Bearer token</option>
              <option value="api-key">API key header</option>
            </select>
          </label>
          {credentials.map((credential) => {
            const mutation = changes.find((change) => change.key === credential.key);
            const presence = credentialPresence.find((entry) => entry.key === credential.key);
            const previouslyConfigured =
              credentialScope === 'system'
                ? presence?.systemConfigured === true
                : configuredKeys.includes(credential.key);
            const configured = previouslyConfigured && !destinationChanged;
            const removable =
              credentialScope === 'system'
                ? presence?.systemConfigured === true
                : presence
                  ? presence.userConfigured
                  : configured;
            return (
              <div key={credential.key} className="space-y-2 rounded border border-border p-3">
                <label className="block text-sm">
                  {credential.label}
                  {credential.required ? ' (required)' : ' (optional)'}
                  <input
                    className={inputClass}
                    type="password"
                    autoComplete="new-password"
                    value={mutation?.action === 'replace' ? mutation.value : ''}
                    disabled={!!busy}
                    placeholder={configured ? 'Configured — leave blank to keep' : 'Not configured'}
                    onChange={(event) =>
                      changeCredential({
                        key: credential.key,
                        action: 'replace',
                        value: event.target.value,
                      })
                    }
                  />
                </label>
                <p className="text-xs text-muted-foreground">
                  {mutation?.action === 'remove'
                    ? credentialScope === 'system'
                      ? 'Will remove the shared system credential.'
                      : 'Will remove your saved credential.'
                    : mutation?.action === 'replace' && mutation.value.trim()
                      ? 'Credential entered. Connection validity is checked separately.'
                      : configured
                        ? 'Configured. Connection validity is checked separately.'
                        : credential.required
                          ? 'Required credential not configured.'
                          : draft.presetId &&
                              ['tavily', 'exa', 'firecrawl'].includes(draft.presetId)
                            ? 'Using free access · Usage limits apply.'
                            : 'Optional credential not configured.'}
                </p>
                {credentialScope === 'user' &&
                  presence?.systemConfigured &&
                  !destinationChanged && (
                    <p className="text-xs text-muted-foreground">
                      Everyone's credential remains available when your personal credential is
                      absent.
                    </p>
                  )}
                {(removable || personalDestinationChanged) && (
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    disabled={!!busy}
                    onClick={() => changeCredential({ key: credential.key, action: 'remove' })}
                  >
                    {personalDestinationChanged
                      ? `Omit ${credential.label} at new URL`
                      : `Remove ${credential.label}`}
                  </Button>
                )}
              </div>
            );
          })}
          <details>
            <summary className="cursor-pointer text-sm font-medium">Advanced headers</summary>
            <div className="mt-3 space-y-3">
              {headers.map((header, index) =>
                header === authentication ? null : (
                  <div key={index} className="space-y-2 rounded border border-border p-3">
                    <label className="block text-sm">
                      Header name
                      <input
                        className={inputClass}
                        disabled={!canManage || !!busy}
                        value={header.name}
                        onChange={(event) =>
                          updateHeader(index, { ...header, name: event.target.value })
                        }
                      />
                    </label>
                    {header.credentialKey ? (
                      <p className="text-xs text-muted-foreground">
                        Value comes from its credential field above.
                      </p>
                    ) : (
                      <label className="block text-sm">
                        Header value
                        <input
                          className={inputClass}
                          disabled={!canManage || !!busy}
                          value={header.value ?? ''}
                          onChange={(event) =>
                            updateHeader(index, { ...header, value: event.target.value })
                          }
                        />
                      </label>
                    )}
                    {!header.credentialKey && (
                      <Button
                        type="button"
                        size="sm"
                        variant="outline"
                        disabled={!canManage || !!busy}
                        onClick={() => {
                          let ordinal = 1;
                          while (credentials.some((item) => item.key === `header-${ordinal}`))
                            ordinal++;
                          const key = `header-${ordinal}`;
                          if (header.value)
                            changeCredential({ key, action: 'replace', value: header.value });
                          update({
                            credentials: [
                              ...credentials,
                              { key, label: header.name || 'Header credential', required: true },
                            ],
                            httpHeaders: headers.map((item, position) =>
                              position === index ? { name: item.name, credentialKey: key } : item
                            ),
                          });
                        }}
                      >
                        Store value as credential
                      </Button>
                    )}
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      disabled={!canManage || !!busy}
                      onClick={() =>
                        update({ httpHeaders: headers.filter((_, position) => position !== index) })
                      }
                    >
                      Remove header
                    </Button>
                  </div>
                )
              )}
            </div>
            <Button
              type="button"
              className="mt-3"
              variant="outline"
              disabled={!canManage || !!busy || headers.length >= 20}
              onClick={() => update({ httpHeaders: [...headers, { name: '', value: '' }] })}
            >
              Add header
            </Button>
          </details>
          <details>
            <summary className="cursor-pointer text-sm font-medium">Advanced</summary>
            <div className="mt-3 space-y-3">
              {canManageSystemCredentials &&
                (!connector || connector.scope === 'system') &&
                credentials.length > 0 && (
                  <label className="block text-sm">
                    Credential scope
                    <select
                      className={inputClass}
                      disabled={!!busy}
                      value={credentialScope}
                      onChange={(event) => {
                        onDraftChanged?.();
                        setCredentialScope(event.target.value as 'user' | 'system');
                        setChanges([]);
                        setResult(null);
                      }}
                    >
                      <option value="user">Just me</option>
                      <option value="system">Everyone</option>
                    </select>
                  </label>
                )}
              {authKind === 'api-key' && authentication && (
                <label className="block text-sm">
                  API key header name
                  <input className={inputClass} value={authentication.name} disabled={!canManage || !!busy}
                    onChange={(event) => updateHeader(headers.indexOf(authentication), { ...authentication, name: event.target.value })} />
                </label>
              )}
              {!connector && (
                <label className="block text-sm">
                  Connector ID
                  <input
                    className={inputClass}
                    disabled={!canManage || !!busy}
                    value={draft.id}
                    onChange={(event) =>
                      update({ id: event.target.value.toLowerCase().replace(/[^a-z0-9-]/g, '') })
                    }
                  />
                </label>
              )}
              <label className="block text-sm">
                Description
                <textarea
                  className={inputClass}
                  disabled={!canManage || !!busy}
                  value={draft.description}
                  onChange={(event) => update({ description: event.target.value })}
                />
              </label>
              <label className="block text-sm">
                Icon URL
                <input
                  className={inputClass}
                  disabled={!canManage || !!busy}
                  value={draft.iconUrl}
                  onChange={(event) => update({ iconUrl: event.target.value })}
                />
              </label>
              <label className="block text-sm">
                MCP protocol version
                <input
                  className={inputClass}
                  disabled={!canManage || !!busy}
                  value={draft.mcpProtocolVersion}
                  onChange={(event) => update({ mcpProtocolVersion: event.target.value })}
                  placeholder="Automatic"
                />
              </label>
              {credentials.map((credential) => (
                <label key={credential.key} className="flex items-center gap-2 text-sm">
                  <input
                    type="checkbox"
                    checked={credential.required}
                    disabled={!canManage || !!busy}
                    onChange={(event) =>
                      update({
                        credentials: credentials.map((item) =>
                          item.key === credential.key
                            ? { ...item, required: event.target.checked }
                            : item
                        ),
                      })
                    }
                  />
                  Require {credential.label} before connecting
                </label>
              ))}
              <details>
                <summary className="cursor-pointer text-sm">Configuration preview</summary>
                <pre className="mt-2 overflow-auto rounded bg-muted p-3 text-xs">
                  {JSON.stringify(
                    { httpUrl: draft.httpUrl, credentials, httpHeaders: headers },
                    null,
                    2
                  )}
                </pre>
              </details>
            </div>
          </details>
          {result && (
            <div role="status" className="rounded border border-border p-3 text-sm">
              {result.message}
              {result.success && result.toolNames?.length ? (
                <ul className="mt-2 list-inside list-disc">
                  {result.toolNames.map((tool) => (
                    <li key={tool}>{tool}</li>
                  ))}
                </ul>
              ) : null}
            </div>
          )}
          {error && (
            <p role="alert" className="text-sm text-destructive">
              {error}
            </p>
          )}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" disabled={!!busy} onClick={onClose}>
              Cancel
            </Button>
            <Button
              type="button"
              variant="outline"
              disabled={!!busy}
              onClick={() => void run('test')}
            >
              {busy === 'test' ? 'Testing…' : 'Test connection'}
            </Button>
            <Button type="button" disabled={!!busy || (sharedDestinationChanged && !sharedUrlChangeAcknowledged)} onClick={() => void run('save')}>
              {busy === 'save' ? 'Saving…' : 'Save'}
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}

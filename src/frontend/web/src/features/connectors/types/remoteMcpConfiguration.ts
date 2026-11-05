import type { RemoteMcpCredential, RemoteMcpHeader } from '../schemas/connectorSchema';

const credentialQueryNames =
  /^(?:api[-_]?key|access[-_]?token|refresh[-_]?token|token|secret|password|authorization|tavilyApiKey|exaApiKey)$/i;
const placeholder = /\$\{|\{\{|%[A-Z_][A-Z0-9_]*%/;
const reservedHeader =
  /^(?:host|content-length|transfer-encoding|connection|keep-alive|proxy-connection|te|trailer|upgrade|proxy-authorization|mcp-session-id|mcp-protocol-version|accept|content-type|last-event-id)$/i;

export function parseRemoteMcpUrl(input: string): URL {
  if (placeholder.test(input))
    throw new Error('Resolve URL placeholders before configuring the remote MCP.');
  let url: URL;
  try {
    url = new URL(input.trim());
  } catch {
    throw new Error('Enter a valid remote MCP URL.');
  }
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.hash)
    throw new Error('Use an HTTP or HTTPS MCP URL without embedded credentials or a fragment.');
  if ([...url.searchParams.keys()].some((key) => credentialQueryNames.test(key)))
    throw new Error(
      'Remove credentials from the URL and configure them as credential-backed headers.'
    );
  return url;
}

export function suggestRemoteMcpIdentity(input: string): { id: string; name: string } {
  const host = parseRemoteMcpUrl(input).hostname.replace(/^www\./i, '');
  return {
    id: host
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, '-')
      .replace(/^-|-$/g, ''),
    name: host,
  };
}

export function isSensitiveRemoteHeader(name: string, value: string): boolean {
  return (
    /authorization|cookie|api[-_]?key|token|secret/i.test(name) ||
    /^(?:Bearer\s|(?:sk|tvly|fc)[-_])|^[A-Za-z0-9_-]{32,}$/i.test(value)
  );
}

export type RemoteImportConfiguration = {
  credentials: RemoteMcpCredential[];
  httpHeaders: RemoteMcpHeader[];
  credentialValues: Record<string, string>;
};

/** Secret values here exist only in the owner's unsaved form and credential administration request. */
export function parseRemoteImportHeaders(value: unknown): RemoteImportConfiguration {
  if (value == null) return { credentials: [], httpHeaders: [], credentialValues: {} };
  if (typeof value !== 'object' || Array.isArray(value))
    throw new Error('Remote headers must be an object of header names and string values.');
  const entries = Object.entries(value);
  if (entries.length > 20) throw new Error('At most 20 remote headers are allowed.');
  const names = new Set<string>();
  const result: RemoteImportConfiguration = {
    credentials: [],
    httpHeaders: [],
    credentialValues: {},
  };
  const credentialValues = new Map<string, string>();
  for (const [name, raw] of entries) {
    if (!/^[!#$%&'*+.^_`|~0-9A-Za-z-]+$/.test(name) || new TextEncoder().encode(name).length > 256)
      throw new Error('A remote header name is invalid or exceeds 256 bytes.');
    if (names.has(name.toLowerCase()))
      throw new Error('Remote header names must be unique, ignoring case.');
    names.add(name.toLowerCase());
    if (reservedHeader.test(name))
      throw new Error(
        'A remote header is controlled by the HTTP transport and cannot be imported.'
      );
    if (typeof raw !== 'string' || /[\x00-\x08\x0a-\x1f\x7f]/.test(raw))
      throw new Error('Remote header values must be strings without invalid control characters.');
    if (placeholder.test(raw))
      throw new Error('Resolve header placeholders in the credential editor before importing.');
    if (new TextEncoder().encode(raw).length > 8192)
      throw new Error('A remote header value exceeds 8 KiB.');
    if (isSensitiveRemoteHeader(name, raw)) {
      const key = `header-${result.credentials.length + 1}`;
      const bearer = /^Bearer /i.test(raw) && name.toLowerCase() === 'authorization';
      result.credentials.push({ key, label: name, required: true });
      result.httpHeaders.push({
        name,
        credentialKey: key,
        ...(bearer ? { prefix: 'Bearer ' } : {}),
      });
      credentialValues.set(key, bearer ? raw.slice(7) : raw);
    } else result.httpHeaders.push({ name, value: raw });
  }
  return { ...result, credentialValues: Object.fromEntries(credentialValues) };
}

import { describe, expect, it } from 'vitest';
import { connectorSchema } from '@/features/connectors/schemas/connectorSchema';
import {
  blankDraft,
  buildSavePayload,
  entryToDraft,
} from '@/features/connectors/types/connectorDraft';
import { parseMcpServersJson } from '@/features/connectors/types/mcpImport';
import {
  parseRemoteMcpUrl,
  suggestRemoteMcpIdentity,
} from '@/features/connectors/types/remoteMcpConfiguration';

describe('remote MCP configuration', () => {
  it('preserves header bindings and optional declarations through editing and toggling', () => {
    const connector = connectorSchema.parse({
      id: 'remote',
      name: 'Remote',
      transport: 'http',
      httpUrl: 'https://example.com/mcp',
      credentials: [{ key: 'api-key', label: 'API key', required: false }],
      httpHeaders: [{ name: 'Authorization', credentialKey: 'api-key', prefix: 'Bearer ' }],
    });
    const payload = buildSavePayload({
      ...entryToDraft(connector),
      enabled: false,
      envKeys: 'OLD_TOKEN',
    });
    expect(payload.credentials).toEqual(connector.credentials);
    expect(payload.httpHeaders).toEqual(connector.httpHeaders);
    expect(payload.envKeys).toEqual([]);
    expect(payload.enabled).toBe(false);
  });

  it('preserves container environment declarations without remote bindings', () => {
    const payload = buildSavePayload({
      ...blankDraft(),
      transport: 'container',
      envKeys: 'TOKEN\nOTHER',
      containerImage: 'example/image',
      localCommand: 'npx',
      localArgs: '-y\npackage',
      containerCommand: 'node',
      containerArgs: 'one\ntwo',
      containerSidecarName: 'sidecar',
      bundlePath: '/bundle',
      serverType: 'node',
      readinessTimeoutSeconds: '8',
      jitCooldownSeconds: '9',
      healthcheckUrl: 'https://health.example',
      healthcheckIntervalMilliseconds: '500',
    });
    expect(payload.envKeys).toEqual(['TOKEN', 'OTHER']);
    expect(payload.credentials).toEqual([]);
    expect(payload.httpHeaders).toEqual([]);
    expect(payload).toMatchObject({
      localCommand: 'npx',
      localArgs: ['-y', 'package'],
      containerImage: 'example/image',
      containerCommand: 'node',
      containerArgs: ['one', 'two'],
      containerSidecarName: 'sidecar',
      bundlePath: '/bundle',
      serverType: 'node',
      readinessTimeoutSeconds: 8,
      jitCooldownSeconds: 9,
      healthcheckUrl: 'https://health.example',
      healthcheckIntervalMilliseconds: 500,
    });
  });

  it('extracts imported authentication into credential administration values, never header literals', () => {
    const { entries, error } = parseMcpServersJson(
      JSON.stringify({
        mcpServers: {
          remote: {
            url: 'https://example.com/mcp',
            headers: { Authorization: 'Bearer synthetic-key', 'X-Mode': 'free' },
          },
        },
      })
    );
    expect(error).toBeUndefined();
    expect(entries[0].credentials).toEqual([
      { key: 'header-1', label: 'Authorization', required: true },
    ]);
    expect(entries[0].httpHeaders).toEqual([
      { name: 'Authorization', credentialKey: 'header-1', prefix: 'Bearer ' },
      { name: 'X-Mode', value: 'free' },
    ]);
    expect(entries[0].credentialValues).toEqual({ 'header-1': 'synthetic-key' });
    expect(JSON.stringify(entries[0].httpHeaders)).not.toContain('synthetic-key');
  });

  it.each([
    { headers: { Host: 'other.example' } },
    { headers: { 'Proxy-Authorization': 'synthetic-key' } },
    { headers: { 'X-Key': 'one', 'x-key': 'two' } },
    { headers: { 'X-Key': 'synthetic\r\nkey' } },
    { headers: { Authorization: '${API_KEY}' } },
    { headers: { 'X-Key': 123 } },
    { ignoredAuthentication: 'synthetic-key' },
  ])('rejects unsupported or unsafe import without echoing values: %j', (extra) => {
    const result = parseMcpServersJson(
      JSON.stringify({ mcpServers: { remote: { url: 'https://example.com/mcp', ...extra } } })
    );
    expect(result.entries).toEqual([]);
    expect(result.error).toBeTruthy();
    expect(result.error).not.toContain('synthetic');
  });

  it.each([
    'https://user:secret@example.com/mcp',
    'https://example.com/mcp?api_key=secret',
    'https://example.com/mcp?tavilyApiKey=secret',
    'file:///tmp/mcp',
    'https://example.com/mcp#secret',
  ])('rejects credential-bearing or invalid URL %s', (url) => {
    expect(() => parseRemoteMcpUrl(url)).toThrow();
  });

  it('suggests identity from hostname without including query or path', () => {
    expect(
      suggestRemoteMcpIdentity('https://mcp.example.com/secret-path?tools=web_search')
    ).toEqual({ id: 'mcp-example-com', name: 'mcp.example.com' });
  });

  it('rejects unsupported provider sign-in routes without blocking ordinary login queries', () => {
    expect(() => parseRemoteMcpUrl('https://mcp.exa.ai/mcp?login')).toThrow(/OAuth/);
    expect(() => parseRemoteMcpUrl('https://mcp.firecrawl.dev/v2/mcp-oauth')).toThrow(/OAuth/);
    expect(parseRemoteMcpUrl('https://example.com/mcp?login').hostname).toBe('example.com');
  });
});

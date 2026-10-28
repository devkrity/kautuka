export type McpImportEntry = {
  id: string;
  name: string;
  transport: 'local' | 'http';
  command: string;
  args: string[];
  url: string;
  envKeys: string[];
};

export type ImportPhase = 'paste' | 'importing' | 'done';
export type ImportResult = { entry: McpImportEntry; success: boolean; error?: string };

export function parseMcpServersJson(raw: string): { entries: McpImportEntry[]; error?: string } {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return { entries: [], error: 'Invalid JSON — check for missing quotes or trailing commas.' };
  }
  if (typeof parsed !== 'object' || parsed === null)
    return { entries: [], error: 'Expected a JSON object at the top level.' };
  const obj = parsed as Record<string, unknown>;
  const servers = obj.mcpServers ?? obj.servers;
  if (!servers || typeof servers !== 'object')
    return {
      entries: [],
      error: 'No "mcpServers" key found. Wrap your servers in { "mcpServers": { … } }.',
    };
  const entries: McpImportEntry[] = [];
  for (const [key, value] of Object.entries(servers as Record<string, unknown>)) {
    if (!value || typeof value !== 'object') continue;
    const v = value as Record<string, unknown>;
    const id = key
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, '-')
      .replace(/(^-|-$)/g, '');
    const name = key.replace(/[-_]/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase());
    const hasUrl = typeof v.url === 'string' && v.url.length > 0;
    entries.push({
      id,
      name,
      transport: hasUrl ? 'http' : 'local',
      command: typeof v.command === 'string' ? v.command : '',
      args: Array.isArray(v.args) ? v.args.map(String) : [],
      url: typeof v.url === 'string' ? v.url : '',
      envKeys: v.env && typeof v.env === 'object' ? Object.keys(v.env as object) : [],
    });
  }
  if (entries.length === 0)
    return { entries: [], error: 'No server entries found inside "mcpServers".' };
  return { entries };
}

export const IMPORT_EXAMPLE = `{
  "mcpServers": {
    "fetch": {
      "command": "uvx",
      "args": ["mcp-server-fetch"]
    },
    "github": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-github"],
      "env": { "GITHUB_TOKEN": "ghp_..." }
    },
    "my-remote": {
      "url": "https://my-mcp-server.example.com/sse"
    }
  }
}`;

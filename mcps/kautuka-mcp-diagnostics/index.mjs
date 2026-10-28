import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
    CallToolRequestSchema,
    ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";

const defaultEnvKeys = ["KAUTUKA_DIAGNOSTICS_ECHO"];
const defaultHttpTargets = [
  "https://www.microsoft.com",
  "https://api.telegram.org",
];

const server = new Server(
  {
    name: "kautuka-mcp-diagnostics",
    version: "0.2.0",
  },
  {
    capabilities: {
      tools: {},
    },
  },
);

server.setRequestHandler(ListToolsRequestSchema, async () => ({
  tools: [
    {
      name: "echo_with_env",
      description:
        "Echoes a message and reports safe diagnostics for selected environment variables.",
      inputSchema: {
        type: "object",
        properties: {
          message: {
            type: "string",
            description: "The message to echo back.",
          },
          envKeys: {
            type: "array",
            description:
              "Optional environment variable names to inspect. Defaults to KAUTUKA_DIAGNOSTICS_ECHO.",
            items: {
              type: "string",
            },
          },
          includeRawValue: {
            type: "boolean",
            description:
              "When true, return raw values for requested keys. Use only for internal debugging.",
          },
        },
        required: ["message"],
        additionalProperties: false,
      },
    },
    {
      name: "http_probe",
      description:
        "Probes one or more HTTP endpoints and reports status or failure diagnostics.",
      inputSchema: {
        type: "object",
        properties: {
          urls: {
            type: "array",
            description:
              "Optional list of URLs to probe. Defaults to a small public connectivity set.",
            items: {
              type: "string",
            },
          },
          timeoutSeconds: {
            type: "integer",
            description: "Optional timeout per request in seconds.",
            minimum: 1,
            maximum: 60,
          },
          method: {
            type: "string",
            description: "HTTP method to use. Defaults to GET.",
          },
        },
        additionalProperties: false,
      },
    },
  ],
}));

server.setRequestHandler(CallToolRequestSchema, async (request) => {
  switch (request.params.name) {
    case "echo_with_env":
      return handleEchoWithEnv(request.params.arguments ?? {});
    case "http_probe":
      return handleHttpProbe(request.params.arguments ?? {});
    default:
      throw new Error(`Unknown tool: ${request.params.name}`);
  }
});

function handleEchoWithEnv(args) {
  const message = typeof args.message === "string" ? args.message : "";
  const includeRawValue = args.includeRawValue === true;
  const envKeys = normalizeEnvKeys(args.envKeys);

  const envDiagnostics = Object.fromEntries(
    envKeys.map((key) => [key, inspectEnvironmentValue(key, includeRawValue)]),
  );

  const payload = {
    echoedMessage: message,
    env: envDiagnostics,
    process: {
      pid: process.pid,
      cwd: process.cwd(),
      nodeVersion: process.version,
    },
  };

  return jsonResult(payload);
}

async function handleHttpProbe(args) {
  const urls = normalizeUrls(args.urls);
  const method = normalizeMethod(args.method);
  const timeoutSeconds = normalizeTimeout(args.timeoutSeconds);
  const results = [];

  for (const url of urls) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), timeoutSeconds * 1000);

    try {
      const response = await fetch(url, {
        method,
        redirect: "follow",
        signal: controller.signal,
      });

      results.push({
        url,
        ok: response.ok,
        status: response.status,
        statusText: response.statusText,
      });
    } catch (error) {
      results.push({
        url,
        ok: false,
        error: formatError(error),
      });
    } finally {
      clearTimeout(timeout);
    }
  }

  const succeeded = results.filter((result) => result.ok).length;
  return jsonResult({
    method,
    timeoutSeconds,
    succeeded,
    failed: results.length - succeeded,
    results,
  });
}

function normalizeEnvKeys(value) {
  if (!Array.isArray(value)) {
    return defaultEnvKeys;
  }

  const keys = value
    .filter((entry) => typeof entry === "string")
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0);

  return keys.length > 0 ? [...new Set(keys)] : defaultEnvKeys;
}

function inspectEnvironmentValue(key, includeRawValue) {
  const value = process.env[key];
  if (typeof value !== "string") {
    return {
      present: false,
      length: 0,
      maskedValue: null,
      rawValue: includeRawValue ? null : undefined,
    };
  }

  const maskedValue = maskValue(value);
  return {
    present: true,
    length: value.length,
    maskedValue,
    rawValue: includeRawValue ? value : undefined,
  };
}

function maskValue(value) {
  if (value.length <= 4) {
    return "*".repeat(value.length);
  }

  return `${value.slice(0, 2)}${"*".repeat(Math.max(1, value.length - 4))}${value.slice(-2)}`;
}

function normalizeUrls(value) {
  if (!Array.isArray(value)) {
    return defaultHttpTargets;
  }

  const urls = value
    .filter((entry) => typeof entry === "string")
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0);

  return urls.length > 0 ? [...new Set(urls)] : defaultHttpTargets;
}

function normalizeMethod(value) {
  if (typeof value !== "string") {
    return "GET";
  }

  const method = value.trim().toUpperCase();
  return method.length === 0 ? "GET" : method;
}

function normalizeTimeout(value) {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    return 10;
  }

  return Math.min(60, Math.max(1, Math.trunc(value)));
}

function formatError(error) {
  if (!(error instanceof Error)) {
    return { name: "Error", message: String(error) };
  }

  return {
    name: error.name,
    message: error.message,
  };
}

function jsonResult(payload) {
  return {
    content: [
      {
        type: "text",
        text: JSON.stringify(payload, null, 2),
      },
    ],
  };
}

const transport = new StdioServerTransport();
await server.connect(transport);

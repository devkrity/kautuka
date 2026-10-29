import assert from "node:assert/strict";
import { after, before, test } from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { fileURLToPath } from "node:url";

const client = new Client(
  { name: "kautuka-mcp-diagnostics-test", version: "1.0.0" },
  { capabilities: {} },
);
const transport = new StdioClientTransport({
  command: process.execPath,
  args: [fileURLToPath(new URL("./index.mjs", import.meta.url))],
  env: { ...process.env, KAUTUKA_DIAGNOSTICS_ECHO: "safe-test-value" },
});

before(async () => client.connect(transport));
after(async () => client.close());

test("diagnostics MCP process lists tools and serves a stdio tool call", async () => {
  const { tools } = await client.listTools();
  assert.deepEqual(
    tools.map(({ name }) => name).sort(),
    ["echo_with_env", "http_probe"],
  );

  const result = await client.callTool({
    name: "echo_with_env",
    arguments: { message: "runtime smoke" },
  });
  assert.equal(result.isError, undefined);
  const content = result.content.find((item) => item.type === "text");
  assert.ok(content);
  const payload = JSON.parse(content.text);
  assert.equal(payload.echoedMessage, "runtime smoke");
  assert.equal(payload.env.KAUTUKA_DIAGNOSTICS_ECHO.present, true);
});

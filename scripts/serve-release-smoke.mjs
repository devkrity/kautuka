#!/usr/bin/env node

import { promises as fs } from "node:fs";
import http from "node:http";
import path from "node:path";

function parseArgs(argv) {
  const options = { host: "0.0.0.0", port: "20300" };
  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (!token.startsWith("--")) {
      throw new Error(`Unexpected argument: ${token}`);
    }

    const key = token.slice(2);
    const value = argv[index + 1];
    if (!value || value.startsWith("--")) {
      throw new Error(`Missing value for --${key}`);
    }

    options[key] = value;
    index += 1;
  }

  return options;
}

const contentTypes = new Map([
  [".exe", "application/vnd.microsoft.portable-executable"],
  [".json", "application/json; charset=utf-8"],
  [".sig", "text/plain; charset=utf-8"],
  [".sh", "text/x-shellscript; charset=utf-8"],
  [".gz", "application/gzip"],
  [".txt", "text/plain; charset=utf-8"],
  [".md", "text/markdown; charset=utf-8"],
]);

function escapeHtml(value) {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

function formatSize(size) {
  if (size < 1024) {
    return `${size} B`;
  }

  if (size < 1024 * 1024) {
    return `${(size / 1024).toFixed(1)} KB`;
  }

  if (size < 1024 * 1024 * 1024) {
    return `${(size / (1024 * 1024)).toFixed(1)} MB`;
  }

  return `${(size / (1024 * 1024 * 1024)).toFixed(1)} GB`;
}

async function renderDirectoryListing(
  rootDir,
  resolvedPath,
  requestPath,
  response,
) {
  const entries = await fs.readdir(resolvedPath, { withFileTypes: true });
  const sortedEntries = [...entries].sort((left, right) =>
    left.name.localeCompare(right.name, undefined, {
      numeric: true,
      sensitivity: "base",
    }),
  );

  const normalizedRequestPath = requestPath.endsWith("/")
    ? requestPath
    : `${requestPath}/`;
  const parentPath =
    normalizedRequestPath === "/"
      ? null
      : normalizedRequestPath.split("/").slice(0, -2).join("/") || "/";

  const rows = await Promise.all(
    sortedEntries.map(async (entry) => {
      const entryPath = path.join(resolvedPath, entry.name);
      const stats = await fs.stat(entryPath);
      const isDirectory = entry.isDirectory();
      const href = `${normalizedRequestPath}${encodeURIComponent(entry.name)}${isDirectory ? "/" : ""}`;

      return `<tr><td><a href="${href}">${escapeHtml(entry.name)}${isDirectory ? "/" : ""}</a></td><td>${isDirectory ? "dir" : formatSize(stats.size)}</td></tr>`;
    }),
  );

  const parentRow = parentPath
    ? `<tr><td><a href="${parentPath}">..</a></td><td>dir</td></tr>`
    : "";

  const relativeDir = path.relative(rootDir, resolvedPath);
  const title = relativeDir ? `/${relativeDir.replaceAll(path.sep, "/")}` : "/";
  const body = `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>Local Release Feed ${escapeHtml(title)}</title>
  <style>
    body { font-family: sans-serif; margin: 2rem; }
    table { border-collapse: collapse; width: 100%; max-width: 64rem; }
    th, td { border-bottom: 1px solid #ddd; padding: 0.6rem 0.8rem; text-align: left; }
    th { font-weight: 700; }
    code { background: #f5f5f5; padding: 0.15rem 0.35rem; }
  </style>
</head>
<body>
  <h1>Local Release Feed</h1>
  <p>Directory: <code>${escapeHtml(title)}</code></p>
  <p><a href="/manifest">Manifest endpoint</a></p>
  <p><a href="/catalog/manifest">Catalog manifest endpoint</a></p>
  <table>
    <thead>
      <tr><th>Name</th><th>Size</th></tr>
    </thead>
    <tbody>
      ${parentRow}
      ${rows.join("\n")}
    </tbody>
  </table>
</body>
</html>`;

  response.writeHead(200, {
    "Content-Type": "text/html; charset=utf-8",
    "Cache-Control": "no-store",
  });
  response.end(body);
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  if (!options.dir) {
    throw new Error("Required argument: --dir <release-output-dir>");
  }

  const rootDir = path.resolve(options.dir);
  const host = options.host;
  const port = Number.parseInt(options.port, 10);

  const server = http.createServer(async (request, response) => {
    try {
      const requestPath = new URL(request.url ?? "/", "http://127.0.0.1")
        .pathname;
      const relativePath =
        requestPath === "/manifest"
          ? "/kautuka-platform-release.json"
          : requestPath === "/manifest.sig"
            ? "/kautuka-platform-release.sig"
          : requestPath === "/catalog/manifest"
            ? "/catalog/manifest.json"
          : requestPath;
      const normalizedPath = path
        .normalize(relativePath)
        .replace(/^([.][.][/\\])+/, "");
      const filePath = path.join(rootDir, normalizedPath);
      const resolvedPath = path.resolve(filePath);

      if (!resolvedPath.startsWith(rootDir)) {
        response.writeHead(403, {
          "Content-Type": "text/plain; charset=utf-8",
        });
        response.end("Forbidden");
        return;
      }

      const stats = await fs.stat(resolvedPath);
      if (stats.isDirectory()) {
        await renderDirectoryListing(
          rootDir,
          resolvedPath,
          requestPath,
          response,
        );
        return;
      }

      const body = await fs.readFile(resolvedPath);
      const extension = path.extname(resolvedPath).toLowerCase();
      response.writeHead(200, {
        "Content-Type":
          contentTypes.get(extension) ?? "application/octet-stream",
        "Cache-Control": "no-store",
      });
      response.end(body);
    } catch (error) {
      const code =
        error && typeof error === "object" && "code" in error
          ? error.code
          : undefined;
      if (code === "ENOENT") {
        response.writeHead(404, {
          "Content-Type": "text/plain; charset=utf-8",
        });
        response.end("Not found");
        return;
      }

      response.writeHead(500, { "Content-Type": "text/plain; charset=utf-8" });
      response.end(
        error instanceof Error ? error.message : "Internal server error",
      );
    }
  });

  server.on("error", (error) => {
    if (
      error &&
      typeof error === "object" &&
      "code" in error &&
      error.code === "EADDRINUSE"
    ) {
      process.stderr.write(
        `error: local release feed port ${host}:${port} is already in use. Stop the existing server or choose a different --port.\n`,
      );
      process.exitCode = 1;
      return;
    }

    throw error;
  });

  server.listen(port, host, () => {
    process.stdout.write(`Serving ${rootDir} on ${host}:${port}\n`);
  });
}

main().catch((error) => {
  process.stderr.write(
    `${error instanceof Error ? error.message : String(error)}\n`,
  );
  process.exitCode = 1;
});

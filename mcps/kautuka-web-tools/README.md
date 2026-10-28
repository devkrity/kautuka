# Kautuka Web Tools

A collection of tools for Kautuka to perform various web-related tasks.

The importable MCPB bundle follows the standard MCPB `uv` runtime layout: bundle metadata in `manifest.json`, Python project metadata in `pyproject.toml`, source in `src/`, and no custom vendored runtime bootstrap.

Python 3.12.13 is pinned in `.python-version`; the supported range is declared
in `pyproject.toml`. Runtime dependency versions are pinned there, and
`uv.lock` locks the complete resolved runtime and development dependency graph
for reproducible builds. The MCPB bundle uses the lock during packaging and
carries it as release provenance, while launch follows the standard MCPB `uv`
runtime layout.

The default fetch profile is `browser_http`: a browser-like HTTP client profile built on `httpx` with richer navigation headers and HTTP/2 enabled. `fetchMode: "http"` remains a compatibility alias for the same profile.

Normalization defaults to a preserve-first DOM path: objectively noisy HTML elements are removed, strongly signaled boilerplate lists are pruned, the remaining HTML is converted with MarkItDown, and a markdown cleanup pass trims obvious junk before chunking. That cleanup now removes promotional blocks, repeated article windows, related-story link clusters, explicit ad markers, and footer/navigation slabs when those patterns are strongly signaled. `normalizationMode: "trafilatura_recall"` is also available as an explicit comparison path, but it is not the default.

The default response mode is `compact`: it returns fetch metadata, document metadata, a bounded item window, and selected evidence chunks. Compact mode also trims item summaries, limits returned item count, shortens selected-chunk bodies, and uses a smaller expansion budget than full mode. Use `responseMode: "full"` when you explicitly need full `sections` and `chunks` surfaces.

Full-mode chunk surfaces are windowed rather than hard-truncated. Use `chunkWindow.nextOptions` with `chunkOffset` and `maxChunkCount` to retrieve later chunk pages.

## Normalization Paths

Two normalization paths are intentionally supported:

- `normalizationMode: "dom"`
  This is the default path. The pipeline converts HTML with MarkItDown, strips strongly signaled boilerplate from the DOM, converts the stripped HTML back through MarkItDown, and then runs markdown-level cleanup to remove repeated blocks, related-story rails, ad/promotional slabs, and footer/navigation noise.
- `normalizationMode: "trafilatura_recall"`
  This is an explicit alternate path. The pipeline asks Trafilatura to extract markdown directly from the full HTML with recall-oriented settings. If that markdown is too thin, the pipeline falls back to the raw MarkItDown result instead of trusting the Trafilatura output blindly.

In short: `dom` is the conservative mainline path, while `trafilatura_recall` remains a supported comparison and recovery path for pages where a direct extractor may work better.

## Scope

This package intentionally stays narrow:

- Fetch a single URL over plain HTTP(S)
- Convert fetched HTML into Markdown with MarkItDown
- Conservatively strip obvious HTML boilerplate before Markdown conversion, with fallback to the raw page if stripping becomes too thin
- Optionally run an explicit `trafilatura_recall` normalization mode for comparison or recovery testing
- Extract compact list items from repeated markdown link and metadata patterns when present
- Parse headings into lightweight sections
- Chunk by structure-aware boundaries using character limits
- Return a canonical chunk surface plus derived lexical retrieval windows

It does not implement browser automation, site crawling, embeddings, or any built-in Kautuka registration.

## Tooling

- Server name: `Kautuka Web Tools`
- Tool name: `kautuka_web_fetch`
- Prompt name: `kautuka_web_fetch_usage`
- Transport: stdio MCP

The server also publishes MCP-native `instructions`, and the tool advertises an `outputSchema`, `title`, and `annotations` so clients can discover the response contract without relying only on prose descriptions.

## Run

```bash
cd mcps/kautuka-web-tools
uv run --python 3.12 kautuka-web-tools
```

## Example Connector Command

Use the package as an external local MCP command. A typical command form is:

```json
{
  "command": "uv",
  "args": [
    "run",
    "--directory",
    "/home/ash/sources/kautuka/mcps/kautuka-web-tools",
    "kautuka-web-tools"
  ]
}
```

## Build MCPB Bundle

Build the importable MCPB bundle artifact with the reference `mcpb` CLI:

```bash
./mcps/kautuka-web-tools/build-mcpb.sh
```

The script validates the directory with `@anthropic-ai/mcpb` and writes the bundle to `artifacts/mcpb/kautuka-web-tools-<version>.mcpb`.

For Kautuka release/publish staging, use the stable-output wrapper:

```bash
bash scripts/stage-built-in-mcpb.sh --output artifacts/mcpb/kautuka-web-tools-<version>.mcpb
```

That script validates the package and writes the bundle to the requested path. Gateway workspace bootstrap also uses the versioned `artifacts/mcpb/kautuka-web-tools-*.mcpb` output as its repo fallback when no copied `built-in-mcpb/kautuka-web-tools.mcpb` exists beside the app.

## Contract Notes

- HTML normalization uses `httpx -> markitdown`, then applies markdown cleanup to remove obvious navigation, promotional blocks, repeated article windows, related-link rails, and footer chrome before retrieval.
- `responseMode: "compact"` is the default output contract and is intended for model consumption.
- `responseMode: "full"` exposes the heavier indexing surfaces (`sections`, `chunks`) when a caller explicitly needs them.
- `normalizationMode: "dom"` is the default preserve-first normalization strategy.
- `normalizationMode: "trafilatura_recall"` is an explicit alternate mode, not the default path, and it uses Trafilatura direct extraction with fallback when the result is too thin.
- `selectedChunks` is the primary evidence surface.
- `items` in compact mode are a bounded window with shortened summaries, and `itemWindow` metadata describing truncation.
- `chunks` in full mode are returned as a bounded window, with `chunkWindow` metadata describing truncation and continuation.
- Character counts are the authoritative v1 sizing metric.
- Token estimates are advisory metadata only.
- Expansion happens after ranking and is bounded by `topK` plus an expansion budget; compact mode intentionally uses a smaller effective expansion budget than full mode.
- Oversized pages return a bounded partial result with `paging` metadata and `nextOptions` instead of hard-failing by default.

## MCP-Native Guidance

The package now exposes usage guidance through three standard MCP surfaces:

- server `instructions` explain the compact-first retrieval pattern
- tool `outputSchema` formalizes the structured response shape
- prompt `kautuka_web_fetch_usage` provides a reusable prompt template for clients that want an explicit tool-use scaffold

The prompt accepts:

- `url` (required)
- `question` (optional)

Use it when a client wants a ready-made instruction block that says how to call `kautuka_web_fetch`, when to stay in compact mode, and how to continue with the returned follow-up options.

## Follow-Up Call Examples

When the result is partial, prefer the structured follow-up arguments already returned in `structuredContent.guidance.followUpCalls`. Typical continuations look like this.

Request the next full-mode chunk window:

```json
{
  "url": "https://example.com/article",
  "responseMode": "full",
  "normalizationMode": "dom",
  "topK": 3,
  "maxItems": 12,
  "maxChunkCount": 128,
  "maxChunkChars": 1800,
  "chunkOverlapChars": 150,
  "timeoutSeconds": 20,
  "fetchMode": "browser_http",
  "expansionBudget": 2,
  "chunkOffset": 128
}
```

Request the next raw-markdown window:

```json
{
  "url": "https://example.com/article",
  "responseMode": "compact",
  "normalizationMode": "dom",
  "topK": 3,
  "maxItems": 12,
  "maxChunkCount": 128,
  "maxChunkChars": 1800,
  "chunkOverlapChars": 150,
  "timeoutSeconds": 20,
  "fetchMode": "browser_http",
  "expansionBudget": 2,
  "includeRawMarkdown": true,
  "markdownOffsetChars": 24000
}
```

Escalate from compact mode to the full indexing surface:

```json
{
  "url": "https://example.com/article",
  "responseMode": "full",
  "normalizationMode": "dom",
  "topK": 3,
  "maxItems": 12,
  "maxChunkCount": 128,
  "maxChunkChars": 1800,
  "chunkOverlapChars": 150,
  "timeoutSeconds": 20,
  "fetchMode": "browser_http",
  "expansionBudget": 2
}
```

Treat these as examples only; the authoritative continuation arguments are the ones returned in the actual tool result.

## Known Tradeoffs

- No JavaScript rendering. Heavily client-rendered sites may return incomplete or low-quality content.
- No robots.txt or crawl-delay handling. This is acceptable for narrow tool use, but not for scaled crawling.
- No browser-fingerprint rotation. Some sites may return degraded or blocked responses.
- MarkItDown-first extraction still favors recall over precision. The cleanup pass now removes several common article-noise patterns, but some portals and heavily templated pages may still leak residual related-content or layout text.
- The `trafilatura_recall` mode is available for comparison, but it can still return output that is too thin on noisy news portals; the default DOM mode is intentionally more conservative.

## Validation

```bash
cd mcps/kautuka-web-tools
uv run --python 3.12 pytest
npx -y @anthropic-ai/mcpb validate .
```

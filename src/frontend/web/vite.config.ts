import react from '@vitejs/plugin-react';
import fs from 'fs';
import { resolve, dirname } from 'path';
import { fileURLToPath } from 'url';
import pino from 'pino';
import { createStream as createRotatingFileStream } from 'rotating-file-stream';
import { defineConfig, type Plugin } from 'vite';
import { packageSourceAliasEntries } from '../scripts/package-source-aliases.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));

const UI_LOG_LEVELS = new Set(['trace', 'debug', 'info', 'warn', 'error', 'fatal']);
// WebHost rather than Gateway: Gateway listens on a Unix socket, and WebHost already
// forwards /api to it. This is also the path the app takes in production, where the
// built SPA is served by WebHost and calls /api same-origin.
const gatewayProxyTarget = 'http://localhost:20229';
const uiLogLevel = 'info';

const createUiLogger = () => {
  const logsDir = resolve(__dirname, '../logs');
  fs.mkdirSync(logsDir, { recursive: true });

  const fileStream = createRotatingFileStream('ui.log', {
    path: logsDir,
    size: '10M',
    interval: '1d',
    maxFiles: 14,
    compress: 'gzip',
  });

  const logger = pino(
    {
      name: 'kautuka-ui',
      level: uiLogLevel,
      base: {
        service: 'kautuka-ui',
        component: 'ui',
      },
    },
    fileStream
  );

  return logger;
};

const uiLogMiddleware = () => {
  const logger = createUiLogger();

  return {
    name: 'kautuka-ui-log-middleware',
    configureServer(server: {
      middlewares: { use: (path: string, fn: (req: any, res: any) => void) => void };
    }) {
      server.middlewares.use('/ui-logs', (req, res) => {
        if (req.method === 'OPTIONS') {
          res.statusCode = 204;
          res.end();
          return;
        }

        if (req.method !== 'POST') {
          res.statusCode = 405;
          res.end();
          return;
        }

        let rawBody = '';
        req.on('data', (chunk: string) => {
          rawBody += chunk;
          if (rawBody.length > 20000) {
            rawBody = rawBody.slice(0, 20000);
          }
        });
        req.on('end', () => {
          try {
            const payload = JSON.parse(rawBody || '{}') as Record<string, unknown>;
            const message = String(payload.message || '')
              .trim()
              .slice(0, 2000);
            if (!message) {
              res.statusCode = 400;
              res.end(JSON.stringify({ detail: 'message is required' }));
              return;
            }

            const levelCandidate = String(payload.level || 'error').toLowerCase();
            const level = UI_LOG_LEVELS.has(levelCandidate) ? levelCandidate : 'error';
            const meta = {
              category: String(payload.category || 'runtime'),
              path: payload.path ? String(payload.path) : undefined,
              method: payload.method ? String(payload.method) : undefined,
              status: typeof payload.status === 'number' ? payload.status : undefined,
              stack: payload.stack ? String(payload.stack).slice(0, 8000) : undefined,
              timestamp: payload.timestamp ? String(payload.timestamp) : new Date().toISOString(),
              tags: typeof payload.tags === 'object' && payload.tags !== null ? payload.tags : {},
            };

            const logFn = (logger as unknown as Record<string, (obj: object, msg: string) => void>)[
              level
            ];
            if (typeof logFn === 'function') {
              logFn(meta, message);
            } else {
              logger.error(meta, message);
            }

            res.setHeader('Content-Type', 'application/json');
            res.statusCode = 202;
            res.end(JSON.stringify({ accepted: true, queued: true }));
          } catch {
            res.statusCode = 400;
            res.end(JSON.stringify({ detail: 'invalid json payload' }));
          }
        });
      });
    },
  };
};

const staticChunkBudget = (maxBytes: number): Plugin => ({
  name: 'kautuka-static-chunk-budget',
  generateBundle(_options, bundle) {
    const applicationSourceRoot = `${resolve(__dirname, 'src').replaceAll('\\', '/')}/`;
    type BundleChunk = Extract<(typeof bundle)[string], { type: 'chunk' }>;
    const chunks: Record<string, BundleChunk> = {};
    for (const [fileName, output] of Object.entries(bundle)) {
      if (output.type === 'chunk') chunks[fileName] = output;
    }
    const applicationRoots = Object.values(chunks).filter((chunk) => {
      const moduleId = chunk.facadeModuleId?.replaceAll('\\', '/') ?? '';
      return chunk.isEntry || moduleId.startsWith(applicationSourceRoot);
    });
    const visited = new Set<string>();
    const oversized: string[] = [];

    const visitStaticImports = (fileName: string) => {
      if (visited.has(fileName)) return;
      visited.add(fileName);

      const chunk = chunks[fileName];
      if (!chunk) return;
      const chunkBytes = new TextEncoder().encode(chunk.code).byteLength;
      if (chunkBytes > maxBytes) {
        oversized.push(`${fileName} (${(chunkBytes / 1000).toFixed(2)} kB)`);
      }
      chunk.imports.forEach(visitStaticImports);
    };

    applicationRoots.forEach((chunk) => visitStaticImports(chunk.fileName));

    if (oversized.length > 0) {
      this.error(
        `Application entry/route static chunks exceed ${(maxBytes / 1000).toFixed(0)} kB:\n${oversized.join('\n')}`
      );
    }
  },
});

export default defineConfig({
  plugins: [react(), uiLogMiddleware(), staticChunkBudget(400_000)],
  build: {
    // Vite's warning includes intentionally lazy Shiki grammars and Mermaid/WASM
    // renderers. Keep their ceiling explicit while the plugin above enforces the
    // tighter 400 kB budget on every application entry and route's static graph.
    chunkSizeWarningLimit: 800,
    rollupOptions: {
      onwarn(warning, defaultHandler) {
        // @protobufjs/inquire uses eval() as a CJS dynamic-require fallback that
        // is never reached in a browser bundle — safe to suppress.
        if (warning.code === 'EVAL' && warning.id?.includes('@protobufjs/inquire')) {
          return;
        }
        defaultHandler(warning);
      },
      output: {
        manualChunks(id) {
          if (!id.includes('node_modules')) return undefined;
          if (id.includes('@xyflow') || id.includes('@dagrejs')) {
            return 'workflow-canvas-vendor';
          }
          if (
            id.includes('/react/') ||
            id.includes('/react-dom/') ||
            id.includes('/scheduler/') ||
            id.includes('@tanstack/react-query')
          ) {
            return 'react-vendor';
          }
          if (
            id.includes('@devkrity/narad') ||
            id.includes('@devkrity/narad-react') ||
            id.includes('/axios/') ||
            id.includes('/zod/')
          ) {
            return 'data-vendor';
          }
          if (
            id.includes('react-markdown') ||
            id.includes('remark-gfm') ||
            id.includes('unified')
          ) {
            return 'markdown-vendor';
          }
          if (id.includes('react-router')) {
            return 'router-vendor';
          }
          if (id.includes('lucide-react')) {
            return 'icons-vendor';
          }
          if (id.includes('@radix-ui')) {
            return 'radix-vendor';
          }
          return undefined;
        },
      },
    },
  },
  server: {
    port: 8001,
    strictPort: true,
    headers: {
      'X-Frame-Options': 'DENY',
      'X-Content-Type-Options': 'nosniff',
      'Cross-Origin-Resource-Policy': 'same-origin',
      'Permissions-Policy': 'camera=(), microphone=(), geolocation=()',
      'Content-Security-Policy':
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self' ws://localhost:*; frame-ancestors 'none'",
    },
    proxy: {
      '/api': {
        target: gatewayProxyTarget,
        changeOrigin: true,
      },
    },
  },
  resolve: {
    alias: [{ find: '@', replacement: resolve(__dirname, './src') }, ...packageSourceAliasEntries],
    dedupe: ['react', 'react-dom', 'react/jsx-runtime'],
  },
});

import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const frontendRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webRoot = resolve(frontendRoot, 'web');

/** Kautuka-only subpath. Protocol packages resolve from node_modules. */
export const packageSourceAliases = {
  '@devkrity/narad-react/kautuka': resolve(webRoot, 'src/features/chat/kautuka/react.ts'),
};

export const packageSourceTsPaths = {
  '@devkrity/narad-react/kautuka': ['src/features/chat/kautuka/react.ts'],
};

/** Vite/Vitest alias entries — subpaths before parent package names. */
export const packageSourceAliasEntries = [
  {
    find: '@devkrity/narad-react/kautuka',
    replacement: resolve(webRoot, 'src/features/chat/kautuka/react.ts'),
  },
];

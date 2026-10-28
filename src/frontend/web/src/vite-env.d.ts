/// <reference types="vite/client" />

declare module '*.css';

interface ImportMetaEnv {
  readonly VITE_KAUTUKA_VERSION?: string;
  readonly VITE_KAUTUKA_SOURCE_SHA?: string;
}

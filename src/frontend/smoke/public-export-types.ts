/**
 * Type-only smoke: consumes emitted .d.ts public APIs.
 * Compiled by `pnpm typecheck:smoke:exports`.
 */
import type { NaradReducer, NaradSessionSnapshot, NaradClientStore } from '@devkrity/narad';
import type { NaradProviderProps, NaradSelectorEqualityFn } from '@devkrity/narad-react';

declare const naradReducer: NaradReducer;
declare const naradSnapshot: NaradSessionSnapshot;
declare const naradStore: NaradClientStore;
declare const naradProviderProps: NaradProviderProps;
declare const naradSelectorEquality: NaradSelectorEqualityFn<string>;

export type PublicExportSmokeTypes = [
  typeof naradReducer,
  typeof naradSnapshot,
  typeof naradStore,
  typeof naradProviderProps,
  typeof naradSelectorEquality,
];

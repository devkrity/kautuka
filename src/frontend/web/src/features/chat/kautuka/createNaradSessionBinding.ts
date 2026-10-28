import {
  createNaradReducer,
  createExternalStore,
  type NaradClientStore,
  type NaradReducer,
  type CreateNaradReducerOptions,
  type ReducerApplyResult,
} from '@devkrity/narad';
import type { NaradExternalStore, NaradSessionSnapshot } from '@devkrity/narad';

export function createNaradSessionBinding(options: CreateNaradReducerOptions): Readonly<{
  reducer: NaradReducer;
  store: NaradClientStore;
}> {
  const reducer = createNaradReducer(options);
  const store = createExternalStore(() => reducer.getSnapshot()) as NaradExternalStore<NaradSessionSnapshot> & {
    publish(next: NaradSessionSnapshot): void;
  };
  let lastSnapshot = reducer.getSnapshot();

  function publishIfChanged(result: ReducerApplyResult): void {
    const nextSnapshot = reducer.getSnapshot();
    const changed =
      result.applied ||
      result.gap ||
      result.resyncRequired ||
      (result.error ?? null) !== (lastSnapshot.lastError ?? null) ||
      nextSnapshot.sessionGap !== lastSnapshot.sessionGap ||
      nextSnapshot.resyncRequired !== lastSnapshot.resyncRequired;
    if (changed && !Object.is(nextSnapshot, lastSnapshot)) {
      store.publish(nextSnapshot);
      lastSnapshot = nextSnapshot;
    }
  }

  const clientStore: NaradClientStore = {
    subscribe: store.subscribe.bind(store),
    getSnapshot: store.getSnapshot.bind(store),
    apply(value: unknown) {
      const result = reducer.apply(value);
      if (result.duplicate) {
        return result;
      }
      publishIfChanged(result);
      return result;
    },
    clearSessionGap() {
      reducer.clearSessionGap();
      publishIfChanged({ applied: true, duplicate: false, gap: false, resyncRequired: false });
    },
    clearResyncRequired() {
      reducer.clearResyncRequired();
      publishIfChanged({ applied: true, duplicate: false, gap: false, resyncRequired: false });
    },
    setActiveProfiles(profiles) {
      reducer.setActiveProfiles(profiles);
      publishIfChanged({ applied: true, duplicate: false, gap: false, resyncRequired: false });
    },
  };

  return { reducer, store: clientStore };
}

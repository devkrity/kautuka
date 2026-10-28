import type { NaradClientStore } from '@devkrity/narad';

export type SessionCatchUpResult =
  | Readonly<{ ok: true; skipped?: boolean }>
  | Readonly<{ ok: false; error: unknown }>;

export async function runSessionCatchUp(options: {
  readonly store: NaradClientStore;
  readonly fetchPages: (
    cursor: number,
    apply: (record: unknown) => void,
    signal: AbortSignal,
  ) => Promise<void>;
  readonly signal: AbortSignal;
  readonly targetRevision?: number | null;
}): Promise<SessionCatchUpResult> {
  const snapshot = options.store.getSnapshot();
  const targetRevision = options.targetRevision ?? null;
  const authorityCursor = snapshot.lastSessionSequence;
  const revisionPointer = snapshot.sessionRevision;

  const revisionAheadOfAuthority =
    revisionPointer !== null && revisionPointer > authorityCursor;
  if (
    targetRevision !== null &&
    authorityCursor >= targetRevision &&
    !revisionAheadOfAuthority &&
    (revisionPointer === null || revisionPointer <= authorityCursor) &&
    !snapshot.sessionGap &&
    !snapshot.resyncRequired
  ) {
    return { ok: true, skipped: true };
  }

  if (snapshot.sessionGap) {
    options.store.clearSessionGap();
  }

  const cursor = options.store.getSnapshot().lastSessionSequence;
  try {
    await options.fetchPages(cursor, (record) => options.store.apply(record), options.signal);
    const afterSnapshot = options.store.getSnapshot();
    if (
      targetRevision !== null &&
      afterSnapshot.lastSessionSequence < targetRevision &&
      afterSnapshot.lastSessionSequence === cursor
    ) {
      return { ok: true, skipped: true };
    }
    options.store.clearResyncRequired();
    return { ok: true };
  } catch (error) {
    return { ok: false, error };
  }
}

export function readExpectedRevision(store: NaradClientStore): number | null {
  const snapshot = store.getSnapshot();
  if (snapshot.sessionRevision === null) {
    return snapshot.lastSessionSequence;
  }
  return Math.max(snapshot.sessionRevision, snapshot.lastSessionSequence);
}

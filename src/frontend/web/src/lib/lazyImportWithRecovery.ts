const dynamicImportRecoveryStoragePrefix = 'kautuka-dynamic-import-recovery:';

const recoverableDynamicImportMessages = [
  'Failed to fetch dynamically imported module',
  'Importing a module script failed',
  'error loading dynamically imported module',
  'failed to load module script',
  'Import failed',
];

export function isRecoverableDynamicImportError(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error ?? '');
  return recoverableDynamicImportMessages.some((candidate) => message.includes(candidate));
}

export function tryRecoverDynamicImport(
  error: unknown,
  importKey: string,
  storage: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'> | undefined = globalThis.sessionStorage,
  locationObject: Pick<Location, 'reload'> | undefined = globalThis.location
): boolean {
  if (!isRecoverableDynamicImportError(error) || !storage || !locationObject) {
    return false;
  }

  const storageKey = `${dynamicImportRecoveryStoragePrefix}${importKey}`;
  if (storage.getItem(storageKey) === '1') {
    storage.removeItem(storageKey);
    return false;
  }

  storage.setItem(storageKey, '1');
  locationObject.reload();
  return true;
}

export function lazyImportWithRecovery<T>(
  importKey: string,
  loader: () => Promise<T>,
  storage: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'> | undefined = globalThis.sessionStorage,
  locationObject: Pick<Location, 'reload'> | undefined = globalThis.location
): Promise<T> {
  const storageKey = `${dynamicImportRecoveryStoragePrefix}${importKey}`;

  return loader()
    .then((module) => {
      storage?.removeItem(storageKey);
      return module;
    })
    .catch((error) => {
      if (tryRecoverDynamicImport(error, importKey, storage, locationObject)) {
        return new Promise<T>(() => {});
      }

      throw error;
    });
}
import { describe, expect, it, vi } from 'vitest';
import {
  isRecoverableDynamicImportError,
  lazyImportWithRecovery,
  tryRecoverDynamicImport,
} from '@/lib/lazyImportWithRecovery';

describe('lazyImportWithRecovery', () => {
  it('detects stale dynamic import failures', () => {
    expect(
      isRecoverableDynamicImportError(
        new TypeError('Failed to fetch dynamically imported module: /assets/LoginPage.js')
      )
    ).toBe(true);
    expect(isRecoverableDynamicImportError(new Error('some other problem'))).toBe(false);
  });

  it('reloads once on the first recoverable import failure', () => {
    const storage = {
      getItem: vi.fn(() => null),
      setItem: vi.fn(),
      removeItem: vi.fn(),
    };
    const locationObject = { reload: vi.fn() };

    const recovered = tryRecoverDynamicImport(
      new TypeError('Failed to fetch dynamically imported module: /assets/LoginPage.js'),
      'page:login',
      storage,
      locationObject
    );

    expect(recovered).toBe(true);
    expect(storage.setItem).toHaveBeenCalledWith(
      'kautuka-dynamic-import-recovery:page:login',
      '1'
    );
    expect(locationObject.reload).toHaveBeenCalledOnce();
  });

  it('does not loop reloads after an attempted recovery', () => {
    const storage = {
      getItem: vi.fn(() => '1'),
      setItem: vi.fn(),
      removeItem: vi.fn(),
    };
    const locationObject = { reload: vi.fn() };

    const recovered = tryRecoverDynamicImport(
      new TypeError('Failed to fetch dynamically imported module: /assets/LoginPage.js'),
      'page:login',
      storage,
      locationObject
    );

    expect(recovered).toBe(false);
    expect(storage.removeItem).toHaveBeenCalledWith('kautuka-dynamic-import-recovery:page:login');
    expect(locationObject.reload).not.toHaveBeenCalled();
  });

  it('clears the recovery flag when the import succeeds', async () => {
    const storage = {
      getItem: vi.fn(() => null),
      setItem: vi.fn(),
      removeItem: vi.fn(),
    };
    const locationObject = { reload: vi.fn() };

    const module = await lazyImportWithRecovery(
      'page:login',
      async () => ({ default: 'ok' }),
      storage,
      locationObject
    );

    expect(module).toEqual({ default: 'ok' });
    expect(storage.removeItem).toHaveBeenCalledWith('kautuka-dynamic-import-recovery:page:login');
  });
});
export function captureLoginCallbackHash(): void {
  if (globalThis.location.pathname !== '/login/callback' || globalThis.location.hash.length === 0) {
    return;
  }

  globalThis.sessionStorage.setItem('kautukaLoginCallbackHash', globalThis.location.hash);
  const url = new URL(globalThis.location.href);
  url.hash = '';
  globalThis.history.replaceState(null, '', `${url.pathname}${url.search}`);
}

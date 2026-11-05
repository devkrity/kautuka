import { getAccessToken } from '@/lib/authState';
import { loginResponseSchema, type LoginResponse } from '../schemas/authSchema';

type Provider = 'google' | 'microsoft';
type Intent = 'login' | 'link';
type Started = { startUrl: string; brokerOrigin: string; state: string; flowId: string; tabHandle: string };
const identifier = /^[A-Za-z0-9_-]{43}$/;
const suspendedMessage = 'The broker operator suspended this installation. Contact the broker operator; provider grants may need to be revoked.';

function headers(intent: Intent, mail = false): Record<string, string> {
  const result: Record<string, string> = { 'Content-Type': 'application/json' };
  if (intent === 'link' || mail) {
    const token = getAccessToken();
    if (!token) throw new Error('Your session expired. Sign in again.');
    result.Authorization = `Bearer ${token}`;
  }
  return result;
}

/** Must be called directly from a click so browsers permit the popup. */
export async function runBoundExternalLogin(
  provider: Provider,
  accountType?: 'organizations' | 'consumers',
  intent: Intent = 'login',
  inputs: { inviteCode?: string; setupToken?: string } = {},
  signal?: AbortSignal,
): Promise<LoginResponse> {
  return loginResponseSchema.parse(await runBoundFlow(provider, accountType, intent, inputs, signal));
}

export async function runBoundMailLogin(connectorId: string, signal?: AbortSignal): Promise<void> {
  const result = await runBoundFlow('google', undefined, 'login', {}, signal, connectorId);
  if (!result || typeof result !== 'object' || (result as { status?: unknown }).status !== 'connected')
    throw new Error('Mail sign-in was not accepted. Start again.');
}

async function runBoundFlow(
  provider: Provider, accountType: 'organizations' | 'consumers' | undefined, intent: Intent,
  inputs: { inviteCode?: string; setupToken?: string }, signal?: AbortSignal, connectorId?: string,
): Promise<unknown> {
  signal?.throwIfAborted();
  const popup = window.open('', '_blank', 'popup,width=520,height=720');
  if (!popup) throw new Error('Allow popups for Kautuka, then try again.');
  let storageKey: string | undefined;
  let cancelConfirmation: (() => void) | undefined;
  let startedMail: Started | undefined;
  let completed = false;
  try {
    const mail = connectorId !== undefined;
    const startPath = mail ? `/api/connectors/${encodeURIComponent(connectorId)}/oauth/bound-begin` : '/api/auth/external/bound-start';
    const start = await fetch(startPath, {
      method: 'POST', headers: headers(intent, mail),
      body: JSON.stringify(mail ? {} : { provider, microsoftAccountType: accountType, intent }),
      signal: requestSignal(signal, 20000),
    });
    if (start.status === 403 && start.headers.get('Kautuka-Installation-Error') === 'installation_suspended')
      throw new Error(suspendedMessage);
    if (!start.ok) throw new Error('Sign-in could not start. Try again.');
    const data = (await start.json()) as Started;
    signal?.throwIfAborted();
    const destination = new URL(data.startUrl);
    if (destination.origin !== data.brokerOrigin || destination.protocol !== 'https:' || destination.pathname !== '/handoff'
      || !identifier.test(data.state) || !identifier.test(data.flowId) || !identifier.test(data.tabHandle)) {
      throw new Error('Sign-in could not start. Try again.');
    }
    if (mail) startedMail = data;
    storageKey = `kautuka-bound-login:${data.state}`;
    sessionStorage.setItem(storageKey, data.tabHandle);
    const confirmation = new Promise<void>((resolve, reject) => {
      const deadline = window.setTimeout(() => finish(new Error('Sign-in timed out. Start again.')), 60000);
      let confirming = false;
      let finished = false;
      function cleanup() {
        window.clearTimeout(deadline);
        window.removeEventListener('message', onMessage);
        signal?.removeEventListener('abort', onAbort);
      }
      cancelConfirmation = cleanup;
      function finish(error?: Error) {
        if (finished) return;
        finished = true;
        cleanup();
        if (error) reject(error); else resolve();
      }
      function onAbort() { finish(new DOMException('Sign-in cancelled.', 'AbortError')); }
      function onMessage(event: MessageEvent<unknown>) {
        if (confirming || event.origin !== data.brokerOrigin || event.source !== popup
          || !event.data || typeof event.data !== 'object') return;
        const message = event.data as { type?: unknown; flowId?: unknown; nonce?: unknown };
        if (message.type !== 'kautuka-broker-candidate' || message.flowId !== data.flowId
          || typeof message.nonce !== 'string' || !identifier.test(message.nonce)) return;
        confirming = true;
        void (async () => {
          try {
            const bind = () => fetch(mail ? '/api/connectors/oauth/bound-bind' : '/api/auth/external/bind-browser', {
              method: 'POST', headers: headers(intent, mail), signal: requestSignal(signal, 15000),
              body: JSON.stringify({ state: data.state, tabHandle: data.tabHandle, nonce: message.nonce }),
            });
            // Binding is idempotent for this nonce. A lost response can be retried safely.
            let response: Response;
            try { response = await bind(); } catch { if (finished) return; response = await bind(); }
            if (response.status === 403 && response.headers.get('Kautuka-Installation-Error') === 'installation_suspended')
              throw new Error(suspendedMessage);
            if (!response.ok) throw new Error('Binding rejected');
            finish();
          } catch (error) { finish(error instanceof Error && error.message === suspendedMessage ? error : new Error('Sign-in could not be confirmed. Start again.')); }
        })();
      }
      window.addEventListener('message', onMessage);
      signal?.addEventListener('abort', onAbort, { once: true });
      if (signal?.aborted) onAbort();
    });
    popup.location.replace(destination.toString());
    await confirmation;
    const deadline = Date.now() + 240000;
    while (Date.now() < deadline) {
      signal?.throwIfAborted();
      const requestHeaders = headers(intent, mail);
      let response: Response;
      try {
        response = await fetch(mail ? '/api/connectors/oauth/bound-complete' : '/api/auth/external/bound-complete', {
          method: 'POST', headers: requestHeaders, signal: requestSignal(signal, 20000),
          body: JSON.stringify({ state: data.state, tabHandle: data.tabHandle,
            ...(!mail && intent === 'login' ? inputs : {}) }),
        });
      } catch {
        signal?.throwIfAborted();
        if (Date.now() + 2000 >= deadline) break;
        await wait(2000, signal);
        continue;
      }
      if (response.status === 403 && response.headers.get('Kautuka-Installation-Error') === 'installation_suspended')
        throw new Error(suspendedMessage);
      if (response.status === 429 && response.headers.get('Kautuka-Admission-Denied') === 'new_account_limit') {
        const seconds = Number(response.headers.get('retry-after'));
        const when = Number.isFinite(seconds) && seconds > 0 ? ` Try again after ${new Date(Date.now() + seconds * 1000).toLocaleString()}.` : ' Try again later.';
        throw new Error(`This installation has reached its new mail account limit.${when} Start a new sign-in then.`);
      }
      if (response.status === 409 && response.headers.get('Kautuka-Admission-Denied') === 'active_account_limit')
        throw new Error('This installation already has five connected mail accounts for this provider. Disconnect one before connecting another.');
      if (mail && response.status === 403 && response.headers.get('Kautuka-Mail-Error') === 'insufficient_scope')
        throw new Error('Grant the requested mail permissions, then start a new sign-in.');
      if (response.status === 202 || response.status === 503 || response.status === 429) {
        const delay = response.status === 429 ? Math.max(2, Number(response.headers.get('retry-after')) || 2) : 2;
        if (Date.now() + delay * 1000 >= deadline) break;
        await wait(delay * 1000, signal);
        continue;
      }
      if (!response.ok) throw new Error('Sign-in was not accepted. Start again.');
      const result: unknown = await response.json();
      signal?.throwIfAborted();
      completed = true;
      return result;
    }
    throw new Error('Sign-in timed out. Start again.');
  } finally {
    cancelConfirmation?.();
    if (startedMail && !completed) {
      const token = getAccessToken();
      if (token) void fetch('/api/connectors/oauth/bound-cancel', { method: 'POST', keepalive: true,
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
        body: JSON.stringify({ state: startedMail.state, tabHandle: startedMail.tabHandle }),
      }).catch(() => { /* Pending sign-in remains removable or expires. */ });
    }
    if (storageKey) sessionStorage.removeItem(storageKey);
    popup.close();
  }
}

function requestSignal(signal: AbortSignal | undefined, timeout: number): AbortSignal {
  const timed = AbortSignal.timeout(timeout);
  return signal ? AbortSignal.any([signal, timed]) : timed;
}

function wait(ms: number, signal?: AbortSignal): Promise<void> {
  signal?.throwIfAborted();
  return new Promise((resolve, reject) => {
    const done = () => { signal?.removeEventListener('abort', cancelled); resolve(); };
    const timer = window.setTimeout(done, ms);
    const cancelled = () => { window.clearTimeout(timer); signal?.removeEventListener('abort', cancelled); reject(new DOMException('Sign-in cancelled.', 'AbortError')); };
    signal?.addEventListener('abort', cancelled, { once: true });
    if (signal?.aborted) cancelled();
  });
}

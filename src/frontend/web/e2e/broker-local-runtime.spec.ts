import { expect, test } from '@playwright/test';

const broker = process.env.KAUTUKA_TEST_LOCAL_BROKER_ORIGIN?.replace(/\/$/, '');
test.skip(!broker || !/^https:\/\/127\.0\.0\.1:\d+$/.test(broker), 'Requires an isolated local Wrangler broker.');
test.use({ ignoreHTTPSErrors: true });

test('a copied sign-in link in another browser cannot bind or follow the original flow', async ({ browser, request }) => {
  const origin = broker!;
  const encode = (value: Uint8Array | ArrayBuffer | string) => Buffer.from(value).toString('base64url');
  const random = () => encode(crypto.getRandomValues(new Uint8Array(32)));
  const key = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, true, ['sign', 'verify']);
  const publicKey = await crypto.subtle.exportKey('jwk', key.publicKey);
  const jwk = { kty: 'EC', crv: 'P-256', x: publicKey.x, y: publicKey.y };
  const id = random(), verifier = random();
  const challenge = encode(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier)));
  async function signed(path: string, value: object) {
    const target = origin + path, body = JSON.stringify(value), now = Math.floor(Date.now() / 1000);
    const head = encode(JSON.stringify({ typ: 'kautuka-installation+jwt', alg: 'ES256', jwk }));
    const payload = encode(JSON.stringify({ aud: origin, htm: 'POST', htu: target, iat: now, exp: now + 60, jti: random(),
      body_hash: encode(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(body))) }));
    const signature = encode(await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, key.privateKey, new TextEncoder().encode(`${head}.${payload}`)));
    return request.post(target, { data: body, ignoreHTTPSErrors: true, headers: { 'content-type': 'application/json', 'Kautuka-Installation-Proof': `${head}.${payload}.${signature}` } });
  }
  expect((await signed('/installation/register', { id })).status()).toBe(204);
  const started = await signed('/installation/flow', { id, provider: 'google', purpose: 'login', state: random(), challenge,
    nonce: random(), gatewayOrigin: 'http://127.0.0.1:4173' });
  expect(started.status()).toBe(200);
  const { flowId } = await started.json() as { flowId: string };
  const first = await browser.newContext({ ignoreHTTPSErrors: true }), second = await browser.newContext({ ignoreHTTPSErrors: true });
  try {
    const original = await first.newPage(), copied = await second.newPage();
    await Promise.all([original.goto(origin), copied.goto(origin)]);
    const post = (page: typeof original, path: string) => page.evaluate(async ({ path, id, flowId }) => {
      const response = await fetch(path, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ id, flowId }) });
      return { status: response.status, body: response.ok ? await response.json() : null };
    }, { path, id, flowId });
    const candidate = await post(original, '/handoff/candidate');
    expect(candidate.status).toBe(200);
    const attacker = await post(copied, '/handoff/candidate');
    expect(attacker.status).toBe(200);
    expect(attacker.body.nonce).not.toBe(candidate.body.nonce);
    expect((await request.post(`${origin}/installation/bind-browser`, { ignoreHTTPSErrors: true,
      data: { id, flowId, nonce: attacker.body.nonce } })).status()).toBe(400);
    expect((await signed('/installation/bind-browser', { id, flowId, nonce: candidate.body.nonce })).status()).toBe(204);
    expect((await post(original, '/handoff/status')).body.status).toBe('ready');
    expect((await post(copied, '/handoff/status')).body.status).toBe('invalid');
    expect((await post(copied, '/handoff/authorize')).status).toBe(409);
    const flowIds: string[] = [];
    for (let index = 0; index < 2; index++) {
      const response = await signed('/installation/flow', { id, provider: 'google', purpose: 'login', state: random(), challenge,
        nonce: random(), gatewayOrigin: 'http://127.0.0.1:4173' });
      expect(response.status()).toBe(200);
      flowIds.push((await response.json()).flowId);
    }
    const third = await first.newPage();
    await third.goto(origin);
    const candidateFor = (page: typeof original, selected: string) => page.evaluate(async ({ id, flowId }) => {
      const response = await fetch('/handoff/candidate', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ id, flowId }) });
      return { status: response.status, body: response.ok ? await response.json() : null };
    }, { id, flowId: selected });
    const [one, two] = await Promise.all([candidateFor(original, flowIds[0]), candidateFor(third, flowIds[1])]);
    expect([one.status, two.status]).toEqual([200, 200]);
    expect((await signed('/installation/bind-browser', { id, flowId: flowIds[0], nonce: one.body.nonce })).status()).toBe(204);
    expect((await signed('/installation/bind-browser', { id, flowId: flowIds[1], nonce: two.body.nonce })).status()).toBe(204);
    const cookies = await first.cookies(origin);
    expect(cookies.filter(cookie => cookie.name.startsWith('__Host-kautuka-broker-')).length).toBe(3);
    const statusFor = (page: typeof original, selected: string) => page.evaluate(async ({ id, flowId }) => {
      const response = await fetch('/handoff/status', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ id, flowId }) });
      return response.json();
    }, { id, flowId: selected });
    expect((await statusFor(original, flowIds[0])).status).toBe('ready');
    expect((await statusFor(third, flowIds[1])).status).toBe('ready');
  } finally { await first.close(); await second.close(); }
});

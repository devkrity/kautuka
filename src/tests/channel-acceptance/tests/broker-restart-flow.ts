import { expect, type APIRequestContext } from '@playwright/test';

export async function startRestartMailFlow(request: APIRequestContext, base: URL, broker: string, headers: Record<string, string>) {
  const begin = await request.post(new URL('/api/connectors/kautuka-google-mail/oauth/bound-begin', base).toString(), { headers, data: {} });
  expect(begin.status(), 'begin mailbox sign-in').toBe(200);
  const flow = await begin.json() as { state: string; tabHandle: string; flowId: string; startUrl: string };
  const id = new URLSearchParams(new URL(flow.startUrl).hash.slice(1)).get('id');
  expect(id).toBeTruthy();
  const brokerHeaders = { origin: broker, 'content-type': 'application/json' };
  const candidate = await request.post(`${broker}/handoff/candidate`, { headers: brokerHeaders, data: { id, flowId: flow.flowId } });
  expect(candidate.status()).toBe(200);
  const nonce = (await candidate.json()).nonce as string;
  expect((await request.post(new URL('/api/connectors/oauth/bound-bind', base).toString(), {
    headers, data: { state: flow.state, tabHandle: flow.tabHandle, nonce },
  })).status()).toBe(204);
  const authorize = await request.post(`${broker}/handoff/authorize`, { headers: brokerHeaders, data: { id, flowId: flow.flowId } });
  expect(authorize.status()).toBe(200);
  const providerState = new URL((await authorize.json()).authorizeUrl).searchParams.get('state');
  expect((await request.post(`${broker}/handoff/callback`, { headers: brokerHeaders,
    data: { state: providerState, code: 'local-provider-code' } })).status()).toBe(204);
  return flow;
}

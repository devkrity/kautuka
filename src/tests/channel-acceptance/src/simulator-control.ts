import type { Provider, ProviderFault, ProviderInboundEvent, ProviderSetup, ProviderSimulatorControl, SimulatorEvidence } from './model.js';
import { compactSimulatorTranscript } from './simulator-evidence.js';

export class HttpProviderSimulatorControl implements ProviderSimulatorControl {
  public constructor(private readonly baseUrl: URL, private readonly controlKey: string) {}
  public async reset(): Promise<void> { await this.request('/control/reset', { method: 'POST' }); }
  public async configure(_provider: Provider, _setup: ProviderSetup): Promise<void> { /* setup is carried by typed scenario metadata; the standalone simulator is reset per scenario. */ }
  public async injectInbound(event: ProviderInboundEvent): Promise<void> {
    await this.request(`/control/inject/${event.provider}`, { method: 'POST', body: JSON.stringify(event.payload) });
  }
  public async scheduleFault(fault: ProviderFault): Promise<void> {
    const target = fault.provider === 'telegram' ? 'telegram/getUpdates' : `${fault.provider}/socket`;
    await this.request('/control/faults', { method: 'POST', body: JSON.stringify({ target, status: fault.kind === 'permanent-failure' ? 400 : 503, kind: fault.kind, count: 1 }) });
  }
  public async exportEvidence(): Promise<SimulatorEvidence> {
    const evidence = await this.request('/control/evidence');
    const transcript = compactSimulatorTranscript(evidence.transcript);
    return {
      providerMode: 'simulator',
      transcript,
      outboundEffects: transcript.filter((entry: { direction?: unknown }) => entry.direction === 'outbound'),
      pendingTelegramUpdates: typeof evidence.pendingTelegramUpdates === 'number' ? evidence.pendingTelegramUpdates : undefined,
    };
  }
  private async request(path: string, init: RequestInit = {}): Promise<any> {
    const response = await fetch(new URL(path, this.baseUrl), { ...init, headers: { 'X-Kautuka-Simulator-Key': this.controlKey, 'Content-Type': 'application/json', ...(init.headers ?? {}) } });
    if (!response.ok) throw new Error(`Simulator control request ${path} failed with ${response.status}.`);
    return response.json();
  }
}

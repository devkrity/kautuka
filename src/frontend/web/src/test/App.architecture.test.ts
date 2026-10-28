import appSource from '../app/App.tsx?raw';
import routesSource from '../app/AppRoutes.tsx?raw';
import authenticationSource from '../app/useAppAuthentication.ts?raw';
import { describe, expect, it } from 'vitest';

describe('App responsibility ownership', () => {
  it('delegates authentication bootstrap and route policy to focused owners', () => {
    expect(appSource).toContain('useAppAuthentication()');
    expect(appSource).toContain('<AppRoutes');
    expect(appSource).not.toContain('refreshViaCookie');
    expect(appSource).not.toContain('<Routes>');

    expect(authenticationSource).toContain('probeGatewayAvailability');
    expect(authenticationSource).toContain('setAuthFailureHandler');
    expect(authenticationSource).toContain('refreshViaCookie');
    expect(routesSource).toContain('function RequireAuth');
    expect(routesSource).toContain('path="/workspace/:agentId"');
    expect(routesSource).toContain('path="/workflows"');
    expect(routesSource).not.toContain('WorkspaceLegacyRedirect');
    expect(routesSource).not.toContain('CopilotBaselinePage');

    expect(appSource.split('\n').length).toBeLessThanOrEqual(720);
    expect(authenticationSource.split('\n').length).toBeLessThanOrEqual(155);
    expect(routesSource.split('\n').length).toBeLessThanOrEqual(240);
  });
});

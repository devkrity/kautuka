import routesSource from '../app/AppRoutes.tsx?raw';
import { describe, expect, it } from 'vitest';

describe('AppRoutes navigation', () => {
  it('registers workspace as the sole product chat route', () => {
    expect(routesSource).toContain('path="/workspace"');
    expect(routesSource).toContain('path="/workspace/:agentId"');
    expect(routesSource).not.toContain('path="/agent"');
    expect(routesSource).not.toContain('path="/agent-v2"');
    expect(routesSource).not.toContain('WorkspaceLegacyRedirect');
    expect(routesSource).not.toContain('CopilotBaselinePage');
  });

  it('registers workflows as a first-class surface outside agent chat', () => {
    expect(routesSource).toContain('path="/workflows"');
    expect(routesSource).toContain('path="/workflows/:workflowId"');
    expect(routesSource).not.toContain('path="/workflows/settings"');
    expect(routesSource).toContain('path="/workflows/:workflowId/instances/:instanceId"');
    expect(routesSource).toContain("import('@/pages/WorkflowsPage')");
    expect(routesSource).not.toMatch(
      /path="\/workflows[\s\S]{0,200}AgentPageV2/,
    );
  });
});

import pageSource from '../pages/AutopilotPage.tsx?raw';
import activitySource from '../features/autopilot/components/AutopilotActivityPanels.tsx?raw';
import editorSource from '../features/autopilot/components/AutopilotScheduleConfigurationEditor.tsx?raw';
import { describe, expect, it } from 'vitest';

describe('Autopilot page responsibility ownership', () => {
  it('delegates schedule configuration to one focused editor', () => {
    expect(pageSource).toContain('<AutopilotScheduleConfigurationEditor');
    expect(pageSource).toContain('<AutopilotActivityPanels');
    expect(pageSource).not.toContain('preRunApprovalTimeoutSeconds: Number');
    expect(pageSource).not.toContain('notifyChannels: event.target.checked');
    expect(editorSource).toContain('preRunApprovalTimeoutSeconds: Number');
    expect(editorSource).toContain('notifyChannels: checked');
    expect(editorSource).toContain('children: React.ReactNode');
    expect(pageSource).not.toContain('runs.map');
    expect(pageSource).not.toContain('inbox.map');
    expect(activitySource).toContain('runs.map');
    expect(activitySource).toContain('inbox.map');
    expect(activitySource).toContain('setShowRunHistory');

    expect(pageSource.split('\n').length).toBeLessThanOrEqual(820);
    expect(editorSource.split('\n').length).toBeLessThanOrEqual(380);
    expect(activitySource.split('\n').length).toBeLessThanOrEqual(465);
  });
});

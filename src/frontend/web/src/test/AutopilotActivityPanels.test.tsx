import {
  AutopilotActivityPanels,
  type AutopilotInboxItem,
  type AutopilotSchedule,
} from '@/features/autopilot';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

describe('AutopilotActivityPanels', () => {
  it('owns schedule actions and inbox disclosure/read behavior', async () => {
    const runNow = vi.fn().mockResolvedValue(undefined);
    const markInboxRead = vi.fn().mockResolvedValue(undefined);
    const schedule = {
      id: 'schedule-1',
      targetType: 'agent',
      targetId: 'agent-1',
      cronExpression: '*/5 * * * *',
      mode: 'on',
      schedulePromptContext: 'Check status',
      isPaused: false,
    } as AutopilotSchedule;
    const notification = {
      id: 'notification-1',
      isRead: false,
      payloadJson: JSON.stringify({ summary: 'Finished successfully' }),
    } as AutopilotInboxItem;

    render(
      <AutopilotActivityPanels
        schedules={[schedule]}
        runs={[]}
        inbox={[notification]}
        agents={[{ id: 'agent-1', name: 'Planner' }]}
        setDraft={vi.fn()}
        setEditingScheduleId={vi.fn()}
        setDraftFromSchedule={vi.fn()}
        setShowScheduleForm={vi.fn()}
        pauseSchedule={vi.fn()}
        resumeSchedule={vi.fn()}
        runNow={runNow}
        deleteSchedule={vi.fn()}
        markInboxRead={markInboxRead}
        mutating={false}
      />
    );

    await userEvent.click(screen.getByRole('button', { name: /run now/i }));
    await userEvent.click(screen.getByRole('button', { name: /inbox notifications/i }));
    await userEvent.click(screen.getByRole('button', { name: 'Mark read' }));

    expect(runNow).toHaveBeenCalledWith('schedule-1');
    expect(screen.getByText('Finished successfully')).toBeInTheDocument();
    expect(markInboxRead).toHaveBeenCalledWith('notification-1');
  });
});

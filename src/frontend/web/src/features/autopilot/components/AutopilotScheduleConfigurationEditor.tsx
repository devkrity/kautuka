import type { SaveAutopilotScheduleRequest } from '../api/autopilot';
import React from 'react';

const CRON_PRESETS: Array<{ label: string; value: string; hint: string }> = [
  { label: 'Every 5 minutes', value: '*/5 * * * *', hint: 'Runs every 5 minutes, all day' },
  { label: 'Every 15 minutes', value: '*/15 * * * *', hint: 'Four times an hour' },
  { label: 'Every hour', value: '0 * * * *', hint: 'Runs at the top of every hour' },
  { label: 'Every 6 hours', value: '0 */6 * * *', hint: 'Four times a day' },
  { label: 'Daily at midnight', value: '0 0 * * *', hint: 'Once a day, right at midnight' },
  { label: 'Daily at 9 am', value: '0 9 * * *', hint: 'Every morning at 9:00 am' },
  { label: 'Weekly (Monday)', value: '0 9 * * 1', hint: 'Every Monday at 9:00 am' },
  { label: 'Custom…', value: 'custom', hint: 'Type your own schedule' },
];

const MODE_OPTIONS = [
  {
    value: 'on',
    label: '▶ Auto-run',
    description: 'Runs automatically on the schedule you choose.',
  },
  {
    value: 'needs_approval',
    label: '✋ Ask me first',
    description: 'You approve each run before it starts.',
  },
  {
    value: 'off',
    label: '⏸ Disabled',
    description: "Scheduling is turned off. It won't run until you enable it.",
  },
] as const;

export function getAutopilotCronLabel(cronExpression: string): string {
  return CRON_PRESETS.find((preset) => preset.value === cronExpression)?.label ?? cronExpression;
}

export function getAutopilotModeLabel(mode: string): string {
  return MODE_OPTIONS.find((option) => option.value === mode)?.label ?? mode;
}

interface AutopilotScheduleConfigurationEditorProps {
  draft: SaveAutopilotScheduleRequest;
  setDraft: React.Dispatch<React.SetStateAction<SaveAutopilotScheduleRequest>>;
  agents: ReadonlyArray<{ id: string; name: string }>;
  workflows?: ReadonlyArray<{ id: string; name: string; hasStartFields?: boolean }>;
  children: React.ReactNode;
}

export function AutopilotScheduleConfigurationEditor({
  draft,
  setDraft,
  agents,
  workflows = [],
  children,
}: Readonly<AutopilotScheduleConfigurationEditorProps>) {
  const [cronPreset, setCronPreset] = React.useState(() =>
    CRON_PRESETS.some(
      (preset) => preset.value === draft.cronExpression && preset.value !== 'custom'
    )
      ? draft.cronExpression
      : 'custom'
  );
  const modeIsOff = draft.mode === 'off';
  const activeCronHint = CRON_PRESETS.find((preset) => preset.value === cronPreset)?.hint;
  const systemTimeZone = Intl.DateTimeFormat().resolvedOptions().timeZone || 'system local time';

  React.useEffect(() => {
    const matchingPreset = CRON_PRESETS.find(
      (preset) => preset.value === draft.cronExpression && preset.value !== 'custom'
    );
    setCronPreset(matchingPreset?.value ?? 'custom');
  }, [draft.cronExpression]);

  const changeCronPreset = (value: string) => {
    setCronPreset(value);
    if (value !== 'custom') {
      setDraft((previous) => ({ ...previous, cronExpression: value }));
    }
  };

  return (
    <>
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <div className="space-y-1">
          <label className="text-sm font-medium" htmlFor="autopilot-target-type">
            What should run?
          </label>
          <p className="text-xs text-muted-foreground">Choose an agent or a workflow.</p>
          <select
            id="autopilot-target-type"
            aria-label="What should run?"
            value={draft.targetType}
            onChange={(event) => {
              const targetType = event.target.value === 'workflow' ? 'workflow' : 'agent';
              setDraft((previous) => ({
                ...previous,
                targetType,
                targetId: '',
                ...(targetType === 'workflow' && previous.targetType !== 'workflow'
                  ? { cronExpression: '0 9 * * *' }
                  : {}),
              }));
            }}
            className="mt-1 w-full rounded-md border border-border bg-background px-3 py-2 text-sm"
          >
            <option value="agent">Agent</option>
            <option value="workflow">Workflow</option>
          </select>
        </div>

        <div className="space-y-1">
          <label className="text-sm font-medium" htmlFor="autopilot-target">
            {draft.targetType === 'workflow' ? 'Which workflow?' : 'Which agent?'}
          </label>
          {draft.targetType === 'agent' ? (
            <p className="text-xs text-muted-foreground">The agent runs directly.</p>
          ) : null}
          {draft.targetType === 'workflow' &&
          workflows.find((item) => item.id === draft.targetId)?.hasStartFields ? (
            <p className="text-xs text-muted-foreground">
              The workflow runs from its saved start fields.
            </p>
          ) : null}
          <select
            id="autopilot-target"
            title="Select schedule target"
            value={draft.targetId}
            onChange={(event) =>
              setDraft((previous) => ({ ...previous, targetId: event.target.value }))
            }
            className="mt-1 w-full rounded-md border border-border bg-background px-3 py-2 text-sm"
            required
          >
            <option value="">— select —</option>
            {draft.targetType === 'workflow'
              ? workflows.map((workflow) => (
                  <option key={workflow.id} value={workflow.id}>
                    {workflow.name}
                  </option>
                ))
              : agents.map((agent) => (
                  <option key={agent.id} value={agent.id}>
                    {agent.name}
                  </option>
                ))}
          </select>
        </div>
      </div>

      <div className="space-y-1">
        <label className="text-sm font-medium" htmlFor="autopilot-cron-preset">
          How often should it run?
        </label>
        <p className="text-xs text-muted-foreground">
          Choose how often the workflow or agent should run.
        </p>
        <select
          id="autopilot-cron-preset"
          title="Select schedule frequency"
          value={cronPreset}
          onChange={(event) => changeCronPreset(event.target.value)}
          className="mt-1 w-full rounded-md border border-border bg-background px-3 py-2 text-sm"
          disabled={modeIsOff}
        >
          {CRON_PRESETS.map((preset) => (
            <option key={preset.value} value={preset.value}>
              {preset.label}
            </option>
          ))}
        </select>
        {activeCronHint && cronPreset !== 'custom' ? (
          <p className="text-xs text-muted-foreground">💡 {activeCronHint}</p>
        ) : null}
        {cronPreset === 'custom' ? (
          <input
            aria-label="Custom schedule expression"
            value={draft.cronExpression}
            onChange={(event) =>
              setDraft((previous) => ({ ...previous, cronExpression: event.target.value }))
            }
            className="mt-1 w-full rounded-md border border-border bg-background px-3 py-2 text-sm font-mono"
            placeholder="*/5 * * * *"
            required
            disabled={modeIsOff}
          />
        ) : null}
        <p className="text-xs text-muted-foreground">
          Uses this machine&apos;s local timezone automatically:{' '}
          <span className="font-medium text-foreground">{systemTimeZone}</span>.
        </p>
      </div>

      {children}

      <div className="space-y-1">
        <div className="text-sm font-medium">How much control do you want?</div>
        <p className="text-xs text-muted-foreground">
          You decide whether to let it run on its own or always ask you first.
        </p>
        <div className="mt-1 grid grid-cols-1 gap-2 sm:grid-cols-3">
          {MODE_OPTIONS.map((option) => (
            <button
              key={option.value}
              type="button"
              onClick={() => setDraft((previous) => ({ ...previous, mode: option.value }))}
              className={`rounded-lg border p-3 text-left transition-colors ${
                draft.mode === option.value
                  ? 'border-primary bg-primary/10'
                  : 'border-border bg-background hover:bg-muted'
              }`}
            >
              <div className="text-sm font-medium">{option.label}</div>
              <div className="mt-0.5 text-xs text-muted-foreground">{option.description}</div>
            </button>
          ))}
        </div>
      </div>

      {modeIsOff ? (
        <div className="rounded-md border border-border bg-muted/40 px-3 py-2 text-xs text-muted-foreground">
          Auto-Pilot is currently disabled. Schedule details and notifications are locked until you
          switch mode back to Auto-run or Ask me first.
        </div>
      ) : null}

      {draft.mode === 'needs_approval' ? (
        <div className="grid grid-cols-1 gap-3 rounded-lg border border-border bg-muted/20 p-4 sm:grid-cols-2">
          <div className="space-y-1">
            <label className="text-sm font-medium" htmlFor="autopilot-approval-expiry">
              Pre-run approval expires
            </label>
            <select
              id="autopilot-approval-expiry"
              value={draft.preRunApprovalExpiryPolicy}
              onChange={(event) =>
                setDraft((previous) => ({
                  ...previous,
                  preRunApprovalExpiryPolicy: event.target.value as
                    'next_occurrence' | 'duration' | 'never',
                }))
              }
              className="w-full rounded-md border border-border bg-background px-3 py-2 text-sm"
            >
              <option value="next_occurrence">At the next scheduled occurrence</option>
              <option value="duration">After a fixed duration</option>
              <option value="never">Never</option>
            </select>
            {draft.preRunApprovalExpiryPolicy === 'never' ? (
              <p className="text-xs text-attention">
                An unresolved approval can block every future occurrence indefinitely.
              </p>
            ) : null}
          </div>
          {draft.preRunApprovalExpiryPolicy === 'duration' ? (
            <div className="space-y-1">
              <label className="text-sm font-medium" htmlFor="autopilot-approval-minutes">
                Approval window in minutes
              </label>
              <input
                id="autopilot-approval-minutes"
                type="number"
                min={1}
                max={10080}
                value={Math.round((draft.preRunApprovalTimeoutSeconds ?? 3600) / 60)}
                onChange={(event) =>
                  setDraft((previous) => ({
                    ...previous,
                    preRunApprovalTimeoutSeconds: Number(event.target.value) * 60,
                  }))
                }
                className="w-full rounded-md border border-border bg-background px-3 py-2 text-sm"
              />
            </div>
          ) : null}
          <OverlapPolicyEditor draft={draft} setDraft={setDraft} />
        </div>
      ) : null}
      {draft.mode === 'on' ? (
        <div className="rounded-lg border border-border bg-muted/20 p-4">
          <OverlapPolicyEditor draft={draft} setDraft={setDraft} />
        </div>
      ) : null}

      <div className="flex flex-wrap gap-4 rounded-lg border border-border bg-muted/30 px-4 py-3">
        <NotificationToggle
          checked={draft.notifyUiInbox}
          disabled={modeIsOff}
          title="Notify me here when done"
          description="You'll get a notification in the inbox below after each run finishes."
          onChange={(checked) => setDraft((previous) => ({ ...previous, notifyUiInbox: checked }))}
        />
        <NotificationToggle
          checked={draft.notifyChannels}
          disabled={modeIsOff}
          title="Send channel message"
          description="Also send a message to your connected channel when the run completes."
          onChange={(checked) => setDraft((previous) => ({ ...previous, notifyChannels: checked }))}
        />
      </div>
    </>
  );
}

function OverlapPolicyEditor({
  draft,
  setDraft,
}: Readonly<Pick<AutopilotScheduleConfigurationEditorProps, 'draft' | 'setDraft'>>) {
  return (
    <div className="space-y-1">
      <label className="text-sm font-medium" htmlFor={`autopilot-overlap-${draft.mode}`}>
        When a previous run is still active
      </label>
      <select
        id={`autopilot-overlap-${draft.mode}`}
        value={draft.overlapPolicy}
        onChange={(event) =>
          setDraft((previous) => ({
            ...previous,
            overlapPolicy: event.target.value as 'skip_and_notify' | 'skip_silently',
          }))
        }
        className="w-full rounded-md border border-border bg-background px-3 py-2 text-sm"
      >
        <option value="skip_and_notify">Skip and notify configured channels</option>
        <option value="skip_silently">Skip without notification</option>
      </select>
    </div>
  );
}

interface NotificationToggleProps {
  checked: boolean;
  disabled: boolean;
  title: string;
  description: string;
  onChange: (checked: boolean) => void;
}

function NotificationToggle({
  checked,
  disabled,
  title,
  description,
  onChange,
}: Readonly<NotificationToggleProps>) {
  return (
    <label className="flex cursor-pointer items-start gap-3">
      <input
        type="checkbox"
        checked={checked}
        onChange={(event) => onChange(event.target.checked)}
        className="mt-0.5 h-4 w-4 accent-primary"
        disabled={disabled}
      />
      <span>
        <span className="block text-sm font-medium">{title}</span>
        <span className="block text-xs text-muted-foreground">{description}</span>
      </span>
    </label>
  );
}

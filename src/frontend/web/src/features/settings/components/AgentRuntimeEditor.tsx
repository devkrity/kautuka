import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { TabsContent } from '@/components/ui/tabs';
import type {
  AgentRuntimeSettings,
  AgentSettingsEntry,
  HitlExpiryAction,
  SystemRuntimeLimitsSettings,
} from '../types/settingsTypes';
import { MAX_PARALLEL_TOOLS_CAP } from '../utils/settingsUtils';

type ClockMode = 'inherit' | 'unlimited' | 'custom';
type BoolMode = 'inherit' | 'enabled' | 'disabled';
type ActionMode = 'inherit' | HitlExpiryAction;

interface AgentRuntimeEditorProps {
  agent: AgentSettingsEntry;
  platformRuntime: SystemRuntimeLimitsSettings;
  onPatch: (updater: (previous: AgentSettingsEntry) => AgentSettingsEntry) => void;
}

function clockMode(value: number | null | undefined): ClockMode {
  if (value == null) {
    return 'inherit';
  }
  return value === 0 ? 'unlimited' : 'custom';
}

function boolMode(value: boolean | null | undefined): BoolMode {
  if (value == null) {
    return 'inherit';
  }
  return value ? 'enabled' : 'disabled';
}

function formatClockDefault(seconds: number, unlimitedLabel: string): string {
  return seconds <= 0 ? unlimitedLabel : `${seconds}s`;
}

function formatBoolDefault(value: boolean): string {
  return value ? 'enabled' : 'disabled';
}

function formatParallelDefault(value: number): string {
  return value <= 0 ? 'unlimited' : String(value);
}

function actionMode(value: HitlExpiryAction | null | undefined): ActionMode {
  return value === 'inform' || value === 'retry' || value === 'abort' ? value : 'inherit';
}

function withRuntime(
  previous: AgentSettingsEntry,
  patch: Partial<AgentRuntimeSettings>
): AgentSettingsEntry {
  return {
    ...previous,
    runtime: {
      maxRunDurationSeconds: previous.runtime?.maxRunDurationSeconds ?? null,
      hitlExpirySeconds: previous.runtime?.hitlExpirySeconds ?? null,
      hitlExpiryAction: previous.runtime?.hitlExpiryAction ?? null,
      allowMultipleToolCalls: previous.runtime?.allowMultipleToolCalls ?? null,
      allowConcurrentInvocation: previous.runtime?.allowConcurrentInvocation ?? null,
      maxParallelTools: previous.runtime?.maxParallelTools ?? null,
      ...patch,
    },
  };
}

export function AgentRuntimeEditor({
  agent,
  platformRuntime,
  onPatch,
}: Readonly<AgentRuntimeEditorProps>) {
  const runtime = agent.runtime ?? {};
  const maxRunMode = clockMode(runtime.maxRunDurationSeconds);
  const hitlMode = clockMode(runtime.hitlExpirySeconds);
  const expiryActionMode = actionMode(runtime.hitlExpiryAction);
  const multipleMode = boolMode(runtime.allowMultipleToolCalls);
  const concurrentMode = boolMode(runtime.allowConcurrentInvocation);
  const maxParallelMode = clockMode(runtime.maxParallelTools);

  return (
    <TabsContent value="runtime" className="space-y-3">
      <div className="settings-subpanel-compact">
        <div className="mb-3 text-sm font-medium">Run limits</div>
        <p className="mb-3 text-xs text-muted-foreground">
          Inherit uses the platform default from Advanced Runtime. 0 means infinite for max run
          duration and never for HITL expiry.
        </p>
        <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
          <div>
            <label
              className="mb-1 block text-xs text-muted-foreground"
              htmlFor={`agent-max-run-mode-${agent.id}`}
            >
              Max run duration
            </label>
            <Select
              id={`agent-max-run-mode-${agent.id}`}
              value={maxRunMode}
              onChange={(event) => {
                const next = event.target.value as ClockMode;
                onPatch((previous) =>
                  withRuntime(previous, {
                    maxRunDurationSeconds:
                      next === 'inherit' ? null : next === 'unlimited' ? 0 : Math.max(1, runtime.maxRunDurationSeconds ?? 600),
                  })
                );
              }}
              options={[
                {
                  value: 'inherit',
                  label: `Inherit platform (${formatClockDefault(platformRuntime.maxRunDurationSeconds, 'unlimited')})`,
                },
                { value: 'unlimited', label: 'Unlimited (0)' },
                { value: 'custom', label: 'Custom seconds' },
              ]}
            />
            {maxRunMode === 'custom' ? (
              <Input
                className="mt-2"
                id={`agent-max-run-seconds-${agent.id}`}
                type="number"
                min={1}
                value={runtime.maxRunDurationSeconds ?? 600}
                onChange={(event) =>
                  onPatch((previous) =>
                    withRuntime(previous, {
                      maxRunDurationSeconds: Math.max(1, Number(event.target.value) || 1),
                    })
                  )
                }
              />
            ) : null}
          </div>
          <div>
            <label
              className="mb-1 block text-xs text-muted-foreground"
              htmlFor={`agent-hitl-expiry-mode-${agent.id}`}
            >
              HITL expiry
            </label>
            <Select
              id={`agent-hitl-expiry-mode-${agent.id}`}
              value={hitlMode}
              onChange={(event) => {
                const next = event.target.value as ClockMode;
                onPatch((previous) =>
                  withRuntime(previous, {
                    hitlExpirySeconds:
                      next === 'inherit' ? null : next === 'unlimited' ? 0 : Math.max(1, runtime.hitlExpirySeconds ?? 300),
                  })
                );
              }}
              options={[
                {
                  value: 'inherit',
                  label: `Inherit platform (${formatClockDefault(platformRuntime.hitlExpirySeconds, 'never')})`,
                },
                { value: 'unlimited', label: 'Never (0)' },
                { value: 'custom', label: 'Custom seconds' },
              ]}
            />
            {hitlMode === 'custom' ? (
              <Input
                className="mt-2"
                id={`agent-hitl-expiry-seconds-${agent.id}`}
                type="number"
                min={1}
                value={runtime.hitlExpirySeconds ?? 300}
                onChange={(event) =>
                  onPatch((previous) =>
                    withRuntime(previous, {
                      hitlExpirySeconds: Math.max(1, Number(event.target.value) || 1),
                    })
                  )
                }
              />
            ) : null}
          </div>
          <div>
            <label
              className="mb-1 block text-xs text-muted-foreground"
              htmlFor={`agent-hitl-expiry-action-${agent.id}`}
            >
              On HITL expiry
            </label>
            <Select
              id={`agent-hitl-expiry-action-${agent.id}`}
              value={expiryActionMode}
              onChange={(event) => {
                const next = event.target.value as ActionMode;
                onPatch((previous) =>
                  withRuntime(previous, {
                    hitlExpiryAction: next === 'inherit' ? null : next,
                  })
                );
              }}
              options={[
                {
                  value: 'inherit',
                  label: `Inherit platform (${platformRuntime.hitlExpiryAction})`,
                },
                { value: 'inform', label: 'Inform' },
                { value: 'retry', label: 'Retry once' },
                { value: 'abort', label: 'Abort' },
              ]}
            />
            <p className="mt-1 text-xs text-muted-foreground">
              Expiry is not a human deny. Abort stops the run; inform resumes with an expired tool
              result; retry creates one replacement interrupt.
            </p>
          </div>
        </div>
      </div>

      <div className="settings-subpanel-compact">
        <div className="mb-3 text-sm font-medium">Tool invocation</div>
        <p className="mb-3 text-xs text-muted-foreground">
          These control how the model may request tools and how this host executes them. Concurrent
          invocation only applies when multiple tool calls are allowed. Max parallel only applies
          when concurrent invocation is on. 0 means no cap.
        </p>
        <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
          <div>
            <label
              className="mb-1 block text-xs text-muted-foreground"
              htmlFor={`agent-allow-multiple-tools-${agent.id}`}
            >
              Allow multiple tool calls
            </label>
            <Select
              id={`agent-allow-multiple-tools-${agent.id}`}
              value={multipleMode}
              onChange={(event) => {
                const next = event.target.value as BoolMode;
                onPatch((previous) =>
                  withRuntime(previous, {
                    allowMultipleToolCalls: next === 'inherit' ? null : next === 'enabled',
                  })
                );
              }}
              options={[
                {
                  value: 'inherit',
                  label: `Inherit platform (${formatBoolDefault(platformRuntime.allowMultipleToolCalls)})`,
                },
                { value: 'enabled', label: 'Enabled' },
                { value: 'disabled', label: 'Disabled' },
              ]}
            />
            <p className="mt-1 text-xs text-muted-foreground">
              When disabled, the model may request at most one tool per turn.
            </p>
          </div>
          <div>
            <label
              className="mb-1 block text-xs text-muted-foreground"
              htmlFor={`agent-allow-concurrent-${agent.id}`}
            >
              Allow concurrent invocation
            </label>
            <Select
              id={`agent-allow-concurrent-${agent.id}`}
              value={concurrentMode}
              onChange={(event) => {
                const next = event.target.value as BoolMode;
                onPatch((previous) =>
                  withRuntime(previous, {
                    allowConcurrentInvocation: next === 'inherit' ? null : next === 'enabled',
                  })
                );
              }}
              options={[
                {
                  value: 'inherit',
                  label: `Inherit platform (${formatBoolDefault(platformRuntime.allowConcurrentInvocation)})`,
                },
                { value: 'enabled', label: 'Enabled' },
                { value: 'disabled', label: 'Disabled' },
              ]}
            />
            <p className="mt-1 text-xs text-muted-foreground">
              When enabled, independent tool calls in one model turn start together instead of one
              after another.
            </p>
          </div>
          <div>
            <label
              className="mb-1 block text-xs text-muted-foreground"
              htmlFor={`agent-max-parallel-mode-${agent.id}`}
            >
              Max parallel tools
            </label>
            <Select
              id={`agent-max-parallel-mode-${agent.id}`}
              value={maxParallelMode}
              onChange={(event) => {
                const next = event.target.value as ClockMode;
                onPatch((previous) =>
                  withRuntime(previous, {
                    maxParallelTools:
                      next === 'inherit'
                        ? null
                        : next === 'unlimited'
                          ? 0
                          : Math.max(1, runtime.maxParallelTools ?? 4),
                  })
                );
              }}
              options={[
                {
                  value: 'inherit',
                  label: `Inherit platform (${formatParallelDefault(platformRuntime.maxParallelTools)})`,
                },
                { value: 'unlimited', label: 'Unlimited (0)' },
                { value: 'custom', label: 'Custom cap' },
              ]}
            />
            {maxParallelMode === 'custom' ? (
              <Input
                className="mt-2"
                id={`agent-max-parallel-tools-${agent.id}`}
                type="number"
                min={1}
                max={MAX_PARALLEL_TOOLS_CAP}
                value={runtime.maxParallelTools && runtime.maxParallelTools > 0 ? runtime.maxParallelTools : 4}
                onChange={(event) =>
                  onPatch((previous) =>
                    withRuntime(previous, {
                      maxParallelTools: Math.min(
                        MAX_PARALLEL_TOOLS_CAP,
                        Math.max(1, Number(event.target.value) || 1)
                      ),
                    })
                  )
                }
              />
            ) : null}
            <p className="mt-1 text-xs text-muted-foreground">
              Caps in-flight tool bodies for this agent run. 1 forces serial execution. Approval
              waits do not occupy a slot.
            </p>
          </div>
        </div>
      </div>
    </TabsContent>
  );
}

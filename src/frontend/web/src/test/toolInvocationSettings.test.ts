import { describe, expect, it } from 'vitest';
import {
  normalizeAgentRuntime,
  normalizeSystemRuntimeLimits,
  MAX_PARALLEL_TOOLS_CAP,
} from '../features/settings/utils/settingsUtils';

describe('tool invocation settings normalize', () => {
  it('fills platform defaults when runtimeLimits are missing', () => {
    expect(normalizeSystemRuntimeLimits()).toEqual({
      modelTimeoutSeconds: 300,
      maxRunDurationSeconds: 0,
      hitlExpirySeconds: 0,
      hitlExpiryAction: 'abort',
      allowMultipleToolCalls: true,
      allowConcurrentInvocation: false,
      maxParallelTools: 0,
    });
  });

  it('treats missing allowMultipleToolCalls as enabled', () => {
    expect(normalizeSystemRuntimeLimits({ allowMultipleToolCalls: undefined }).allowMultipleToolCalls).toBe(
      true
    );
  });

  it('clamps platform max parallel tools', () => {
    expect(normalizeSystemRuntimeLimits({ maxParallelTools: -2 }).maxParallelTools).toBe(0);
    expect(
      normalizeSystemRuntimeLimits({ maxParallelTools: MAX_PARALLEL_TOOLS_CAP + 8 }).maxParallelTools
    ).toBe(MAX_PARALLEL_TOOLS_CAP);
  });

  it('keeps agent nulls as inherit and persists explicit false', () => {
    expect(normalizeAgentRuntime(undefined)).toEqual({
      maxRunDurationSeconds: null,
      hitlExpirySeconds: null,
      hitlExpiryAction: null,
      allowMultipleToolCalls: null,
      allowConcurrentInvocation: null,
      maxParallelTools: null,
    });

    expect(
      normalizeAgentRuntime({
        allowMultipleToolCalls: false,
        allowConcurrentInvocation: true,
        maxParallelTools: 3,
      })
    ).toEqual({
      maxRunDurationSeconds: null,
      hitlExpirySeconds: null,
      hitlExpiryAction: null,
      allowMultipleToolCalls: false,
      allowConcurrentInvocation: true,
      maxParallelTools: 3,
    });
  });
});

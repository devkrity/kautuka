import type { AgentSettingsEntry } from '../types/settingsTypes';

export type ToolPolicyCategory = 'connector' | 'skill' | 'superexec' | 'other';

export type ToolPolicyReasonCode =
  | 'allowed'
  | 'sandbox_required'
  | 'superexec_disabled'
  | 'uncategorized_category_denied'
  | 'unknown_category_denied';

export interface ToolPolicyDecision {
  allowed: boolean;
  reasonCode: ToolPolicyReasonCode;
  message: string;
}

export function requiresSandbox(category: ToolPolicyCategory): boolean {
  switch (category) {
    case 'connector':
      return false;
    case 'skill':
    case 'superexec':
    case 'other':
      return true;
    default:
      return true;
  }
}

export function evaluateToolExposure(
  category: ToolPolicyCategory,
  agent: Pick<AgentSettingsEntry, 'sandbox' | 'superExecMode'>
): ToolPolicyDecision {
  if (!isKnownCategory(category)) {
    return denied('unknown_category_denied', 'Tool category is unknown and is denied by policy.');
  }

  if (category === 'other') {
    return denied('uncategorized_category_denied', 'Uncategorized tools are denied by policy.');
  }

  if (requiresSandbox(category) && !agent.sandbox.enabled) {
    return denied('sandbox_required', 'Sandbox is required by policy for this capability.');
  }

  if (category === 'superexec' && agent.superExecMode === 'disabled') {
    return denied('superexec_disabled', 'SuperExec is disabled for this agent.');
  }

  return allowed('Allowed by policy.');
}

function isKnownCategory(category: ToolPolicyCategory): boolean {
  return category === 'connector' || category === 'skill' || category === 'superexec' || category === 'other';
}

function allowed(message: string): ToolPolicyDecision {
  return {
    allowed: true,
    reasonCode: 'allowed',
    message,
  };
}

function denied(reasonCode: ToolPolicyReasonCode, message: string): ToolPolicyDecision {
  return {
    allowed: false,
    reasonCode,
    message,
  };
}
export type ApprovalAction = 'approve' | 'deny' | 'stop-run';

export function expectedDecision(action: ApprovalAction): 'approved' | 'denied' | 'stopped' {
  switch (action) {
    case 'approve':
      return 'approved';
    case 'deny':
      return 'denied';
    case 'stop-run':
      return 'stopped';
  }
}

/** Product/channel action token value (underscore style on the wire). */
export function nativeApprovalAction(action: ApprovalAction): string {
  switch (action) {
    case 'approve':
      return 'approve';
    case 'deny':
      return 'deny';
    case 'stop-run':
      return 'stop_run';
  }
}

export function approvalActionSelector(action: ApprovalAction): string {
  switch (action) {
    case 'approve':
      return '[data-approval-action="approve"]';
    case 'deny':
      return '[data-approval-action="deny"]';
    case 'stop-run':
      return '[data-approval-action="stop_run"]';
  }
}

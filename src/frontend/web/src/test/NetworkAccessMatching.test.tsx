// @vitest-environment jsdom
import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { PendingApprovalCard } from '@/features/network-access/components/PendingApprovalCard';
import type { NetworkAccessRequestDto } from '@/features/network-access/networkAccess';
import { UserNetworkPrivilegesForm } from '@/features/network-access/components/UserNetworkPrivilegesForm';
import { RulesList } from '@/features/network-access/components/RulesList';
import { RuleScopeEditor } from '@/features/network-access/components/RuleScopeEditor';
import { GroupedPendingCards } from '@/features/network-access/components/GroupedPendingCards';
import type { NetworkAccessRuleDto } from '@/features/network-access/networkAccess';

vi.mock('@/features/settings/api/queries', () => ({ useAgents: () => ({ data: [] }) }));
vi.mock('@/features/connectors/api/queries', () => ({ useConnectors: () => ({ data: [{ id: 'web', name: 'Web Tools', scope: 'user', ownerUserId: 'alice' }] }) }));

describe('network access user scopes', () => {
  it('uses the revision returned by each decision when allowing a group', async () => {
    const request: NetworkAccessRequestDto = {
      id: 'one', userId: 'alice', agentId: 'agent-1', agentDisplayName: 'My Assistant',
      attribution: 'user-agent', destination: 'one.example', destKind: 'host',
      port: 443, actorKind: 'agent', firstSeenUtc: '2026-01-01T00:00:00Z',
      lastSeenUtc: '2026-01-01T00:00:00Z', attempts: 1, status: 'pending', origin: 'container',
    };
    const onDecide = vi.fn().mockImplementation(async (decision: { expectedRevision: number }) => {
      const expected = 4 + onDecide.mock.calls.length - 1;
      if (decision.expectedRevision !== expected) throw new Error('revision conflict');
      return { revision: expected + 1 };
    });
    render(<GroupedPendingCards requests={[request, { ...request, id: 'two', destination: 'two.example' }]}
      currentUser={{ id: 'alice', role: 'user' }} userPrivileges={{ canApproveOwn: true }} expectedRevision={4} onDecide={onDecide} />);
    fireEvent.click(screen.getByRole('button', { name: 'Allow all 2' }));
    await waitFor(() => expect(onDecide).toHaveBeenCalledTimes(2));
    expect(onDecide).toHaveBeenNthCalledWith(2, expect.objectContaining({ expectedRevision: 5 }), 'two');
  });
  it('stops the group when a decision fails', async () => {
    const request: NetworkAccessRequestDto = {
      id: 'one', userId: 'alice', agentId: 'agent-1', agentDisplayName: 'My Assistant',
      attribution: 'user-agent', destination: 'one.example', destKind: 'host',
      port: 443, actorKind: 'agent', firstSeenUtc: '2026-01-01T00:00:00Z',
      lastSeenUtc: '2026-01-01T00:00:00Z', attempts: 1, status: 'pending', origin: 'container',
    };
    const onDecide = vi.fn().mockRejectedValue(new Error('revision conflict'));
    render(<GroupedPendingCards requests={[request, { ...request, id: 'two', destination: 'two.example' }]}
      currentUser={{ id: 'alice', role: 'user' }} userPrivileges={{ canApproveOwn: true }} expectedRevision={4} onDecide={onDecide} />);
    fireEvent.click(screen.getByRole('button', { name: 'Allow all 2' }));
    await waitFor(() => expect(onDecide).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Allow all 2' })).toBeEnabled());
  });
  it('does not group requests belonging to different users with the same agent ID', () => {
    const request: NetworkAccessRequestDto = {
      id: 'one', userId: 'alice', agentId: 'agent-1', agentDisplayName: 'Assistant',
      attribution: 'user-agent', destination: 'one.example', destKind: 'host',
      port: 443, actorKind: 'agent', firstSeenUtc: '2026-01-01T00:00:00Z',
      lastSeenUtc: '2026-01-01T00:00:00Z', attempts: 1, status: 'pending', origin: 'container',
    };
    render(<GroupedPendingCards requests={[request, { ...request, id: 'two', userId: 'bob', destination: 'two.example' }]}
      currentUser={{ id: 'admin', role: 'admin' }} expectedRevision={4} onDecide={vi.fn()} />);
    expect(screen.queryByRole('button', { name: 'Allow all 2' })).not.toBeInTheDocument();
  });
  it('explains when a system request creates a platform-wide allow or block', () => {
    const request: NetworkAccessRequestDto = {
      id: 'system-request', userId: 'system', agentId: 'gateway',
      attribution: 'user-agent', destination: 'example.com', destKind: 'host',
      port: 443, actorKind: 'first-party', firstSeenUtc: '2026-01-01T00:00:00Z',
      lastSeenUtc: '2026-01-01T00:00:00Z', attempts: 1, status: 'pending', origin: 'gateway',
    };
    render(<PendingApprovalCard request={request} currentUser={{ id: 'admin', role: 'admin' }} expectedRevision={4} onDecide={vi.fn()} />);
    expect(screen.getByRole('button', { name: 'Allow for everyone' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Block for everyone' })).toBeInTheDocument();
    expect(screen.getByText(/block overrides other permissions/i)).toBeInTheDocument();
    expect(screen.getByRole('heading')).toHaveClass('break-all');
  });
  it('explains each approval scope without exposing runtime connector IDs', async () => {
    const request: NetworkAccessRequestDto = {
      id: 'request-scopes', userId: 'alice', userDisplayName: 'Alice', agentId: 'agent-1',
      agentDisplayName: 'My Assistant', connectorId: 'alice::web', connectorDisplayName: 'Web Tools',
      attribution: 'user-agent', destination: 'www.bbc.com', destKind: 'host',
      port: 443, actorKind: 'connector', firstSeenUtc: '2026-01-01T00:00:00Z',
      lastSeenUtc: '2026-01-01T00:00:00Z', attempts: 1, status: 'pending', origin: 'container',
    };
    const onDecide = vi.fn().mockResolvedValue(undefined);
    render(<PendingApprovalCard request={request} currentUser={{ id: 'alice', role: 'user' }}
      userPrivileges={{ canApproveOwn: true }} expectedRevision={4} onDecide={onDecide} />);
    expect(screen.getByRole('button', { name: 'Allow My Assistant to access this site' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Allow this site whenever I use Web Tools' }));
    await waitFor(() => expect(onDecide).toHaveBeenCalledWith(expect.objectContaining({ scope: 'connector' })));
    expect(screen.getByRole('button', { name: 'Allow this site for everything I use' })).toBeInTheDocument();
    expect(screen.queryByText(/alice::web/)).not.toBeInTheDocument();
  });
  it('offers one permission to manage own network access', () => {
    const save = vi.fn();
    render(<UserNetworkPrivilegesForm canApproveOwn={false} isAdminViewer onSave={save} />);
    fireEvent.click(screen.getByRole('checkbox', { name: 'Can manage their own network access' }));
    expect(save).toHaveBeenCalledWith(expect.objectContaining({ canApproveOwn: true }));
    expect(screen.getAllByRole('checkbox')).toHaveLength(1);
  });
  it('restores the privilege checkbox and shows an error when saving fails', async () => {
    const save = vi.fn().mockRejectedValue(new Error('offline'));
    render(<UserNetworkPrivilegesForm canApproveOwn={false} isAdminViewer onSave={save} />);
    const checkbox = screen.getByRole('checkbox', { name: 'Can manage their own network access' });
    fireEvent.click(checkbox);
    expect(await screen.findByRole('alert')).toHaveTextContent('could not be confirmed');
    expect(checkbox).not.toBeChecked();
  });

  it('allows a user to edit the scope of their connector rule', () => {
    const rule: NetworkAccessRuleDto = {
      id: 'rule-1', scopeKind: 'user', userId: 'alice', connectorId: 'web',
      destination: 'example.com', destKind: 'host', port: 443, allowed: true,
      allowLan: false, source: 'user', paused: false, summaryText: '', decidedBy: 'alice',
      decidedAtUtc: '2026-01-01T00:00:00Z', openInternetBadge: false,
    };
    const edit = vi.fn();
    render(<RulesList rules={[rule]} expectedRevision={4} currentUser={{ id: 'alice', role: 'user' }}
      onDeleteRule={vi.fn()} onEditRule={edit} canManageOwn />);
    fireEvent.click(screen.getByRole('button', { name: 'Edit' }));
    expect(edit).toHaveBeenCalledWith(rule);
  });

  it('asks for confirmation before removing a rule', async () => {
    const rule: NetworkAccessRuleDto = {
      id: 'rule-confirm', scopeKind: 'user', userId: 'alice', destination: 'example.com', destKind: 'host', port: 443,
      allowed: true, allowLan: false, source: 'user', paused: false, summaryText: '', decidedBy: 'alice',
      decidedAtUtc: '2026-01-01T00:00:00Z', openInternetBadge: false,
    };
    const remove = vi.fn().mockResolvedValue(undefined);
    render(<RulesList rules={[rule]} expectedRevision={4} currentUser={{ id: 'alice', role: 'user' }} onDeleteRule={remove} canManageOwn />);
    fireEvent.click(screen.getByRole('button', { name: 'Remove' }));
    expect(remove).not.toHaveBeenCalled();
    expect(screen.getByRole('dialog')).toHaveTextContent('example.com');
    fireEvent.click(screen.getByRole('button', { name: 'Confirm removal' }));
    await waitFor(() => expect(remove).toHaveBeenCalledWith('rule-confirm', 4));
  });
  it('asks for confirmation before blocking a built-in rule', async () => {
    const rule: NetworkAccessRuleDto = {
      id: 'seed-confirm', scopeKind: 'system', destination: 'example.com', destKind: 'host', port: 443,
      allowed: true, allowLan: false, source: 'seed', paused: false, summaryText: '', decidedBy: 'system',
      decidedAtUtc: '2026-01-01T00:00:00Z', openInternetBadge: false,
    };
    const block = vi.fn().mockResolvedValue(undefined);
    render(<RulesList rules={[rule]} expectedRevision={4} currentUser={{ id: 'admin', role: 'admin' }} onDeleteRule={vi.fn()} onBlockSeedRule={block} />);
    await userEvent.setup().click(screen.getByRole('tab', { name: 'Built in (1)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Block' }));
    expect(screen.getByRole('heading', { name: 'Block built-in permission?' })).toBeInTheDocument();
    expect(block).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Confirm block' }));
    await waitFor(() => expect(block).toHaveBeenCalledWith(rule));
  });

  it('lets a user remove their own block over an administrator allow', async () => {
    const rule: NetworkAccessRuleDto = {
      id: 'tightening', scopeKind: 'agent', userId: 'alice', agentId: 'secretary',
      destination: 'example.com', destKind: 'host', port: 443, allowed: false,
      allowLan: false, source: 'user', paused: false, summaryText: '', decidedBy: 'alice',
      decidedAtUtc: '2026-01-01T00:00:00Z', openInternetBadge: false, protectedFromUser: false,
    };
    const remove = vi.fn().mockResolvedValue(undefined);
    render(<RulesList rules={[rule]} expectedRevision={4} currentUser={{ id: 'alice', role: 'user' }}
      onDeleteRule={remove} onEditRule={vi.fn()} onBlockSeedRule={vi.fn()} canManageOwn />);
    await userEvent.setup().click(screen.getByRole('tab', { name: /Blocked/ }));
    expect(screen.getByRole('button', { name: 'Edit' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Remove' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Remove' }));
    fireEvent.click(screen.getByRole('button', { name: 'Confirm removal' }));
    await waitFor(() => expect(remove).toHaveBeenCalledWith('tightening', 4));
  });

  it('lets a user block an administrator rule but not edit or remove it', async () => {
    const rule: NetworkAccessRuleDto = {
      id: 'admin-rule', scopeKind: 'agent', userId: 'alice', agentId: 'secretary',
      destination: 'example.com', destKind: 'host', port: 443, allowed: true,
      allowLan: false, source: 'user', paused: false, summaryText: '', decidedBy: 'admin',
      decidedAtUtc: '2026-01-01T00:00:00Z', openInternetBadge: false, protectedFromUser: true,
    };
    const block = vi.fn().mockResolvedValue(undefined);
    render(<RulesList rules={[rule]} expectedRevision={4} currentUser={{ id: 'alice', role: 'user' }}
      onDeleteRule={vi.fn()} onEditRule={vi.fn()} onBlockSeedRule={block} canManageOwn />);
    expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Remove' })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Block' }));
    expect(screen.getByRole('heading', { name: 'Block access?' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Confirm block' }));
    await waitFor(() => expect(block).toHaveBeenCalledWith(rule));
  });

  it('submits a scope change as one replacement of the existing rule', async () => {
    const rule: NetworkAccessRuleDto = {
      id: 'rule-1', scopeKind: 'user', userId: 'alice', connectorId: 'web',
      destination: 'example.com', destKind: 'host', port: 443, allowed: true,
      allowLan: false, source: 'user', paused: false, summaryText: '', decidedBy: 'alice',
      decidedAtUtc: '2026-01-01T00:00:00Z', openInternetBadge: false,
    };
    const save = vi.fn().mockResolvedValue(undefined);
    render(<RuleScopeEditor rule={rule} userId="alice" isAdmin={false} expectedRevision={4} onCancel={vi.fn()} onSave={save} />);
    fireEvent.change(screen.getByLabelText('Applies to'), { target: { value: 'user' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save rule' }));
    await waitFor(() => expect(save).toHaveBeenCalledWith(expect.objectContaining({ scopeKind: 'user', userId: 'alice', connectorId: undefined, replaceRuleId: 'rule-1', expectedRevision: 4 })));
  });

  it('resets editor choices when switching to another rule', async () => {
    const rule: NetworkAccessRuleDto = {
      id: 'rule-one', scopeKind: 'user', userId: 'alice', connectorId: 'web',
      destination: 'one.example', destKind: 'host', port: 443, allowed: true,
      allowLan: false, source: 'user', paused: false, summaryText: '', decidedBy: 'alice',
      decidedAtUtc: '2026-01-01T00:00:00Z', openInternetBadge: false,
    };
    const save = vi.fn().mockResolvedValue(undefined);
    const editor = (value: NetworkAccessRuleDto) => <RuleScopeEditor rule={value} userId="alice" isAdmin={false} expectedRevision={4} onCancel={vi.fn()} onSave={save} />;
    const { rerender } = render(editor(rule));
    fireEvent.change(screen.getByLabelText('Decision'), { target: { value: 'block' } });
    rerender(editor({ ...rule, id: 'rule-two', destination: 'two.example', connectorId: undefined, allowed: true }));
    expect(screen.getByLabelText('Applies to')).toHaveValue('user');
    expect(screen.getByLabelText('Decision')).toHaveValue('allow');
    fireEvent.click(screen.getByRole('button', { name: 'Save rule' }));
    await waitFor(() => expect(save).toHaveBeenCalledWith(expect.objectContaining({ replaceRuleId: 'rule-two', allowed: true, scopeKind: 'user' })));
  });

  it('lets the owner approve their connector without agent context regardless of availability', async () => {
    const request: NetworkAccessRequestDto = {
      id: 'request-1', userId: 'alice', connectorId: 'web', attribution: 'user', destination: 'example.com', destKind: 'host', port: 443,
      actorKind: 'connector', firstSeenUtc: '2026-01-01T00:00:00Z',
      lastSeenUtc: '2026-01-01T00:00:00Z', attempts: 1, status: 'pending', origin: 'container',
    };
    const onDecide = vi.fn().mockResolvedValue(undefined);
    render(<PendingApprovalCard request={request} currentUser={{ id: 'alice', role: 'user' }}
      userPrivileges={{ canApproveOwn: true }} expectedRevision={4} onDecide={onDecide} />);
    fireEvent.click(screen.getByRole('button', { name: 'Allow access' }));
    await waitFor(() => expect(onDecide).toHaveBeenCalledWith(expect.objectContaining({ allow: true, scope: 'connector', expectedRevision: 4 })));
    expect(screen.queryByText(/Waiting for an administrator/i)).not.toBeInTheDocument();
  });
});
